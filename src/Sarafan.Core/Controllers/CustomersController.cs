// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

using Sarafan.Core.Data;
using Sarafan.Core.Models;
using Sarafan.Core.RestModels;
using Sarafan.Core.Services;

namespace Sarafan.Core.Controllers;

[Authorize]
[Route("api/v1/customers/me")]
public sealed class CustomersController(
    AppDbContext database,
    TimeProvider timeProvider,
    ConsentService consents,
    SarafanProblemDetailsFactory problemDetailsFactory) : SarafanControllerBase(problemDetailsFactory)
{
    private const int MaxPhotoSize = 5 * 1024 * 1024;
    private static readonly IReadOnlyDictionary<string, Func<ReadOnlyMemory<byte>, bool>> PhotoSignatures =
        new Dictionary<string, Func<ReadOnlyMemory<byte>, bool>>(StringComparer.OrdinalIgnoreCase)
        {
            ["image/jpeg"] = data => data.Length >= 3
                && data.Span[0] == 0xff && data.Span[1] == 0xd8 && data.Span[2] == 0xff,
            ["image/png"] = data => data.Length >= 8
                && data.Span[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a }),
            ["image/webp"] = data => data.Length >= 12
                && data.Span[..4].SequenceEqual("RIFF"u8)
                && data.Span.Slice(8, 4).SequenceEqual("WEBP"u8)
        };

    [HttpGet]
    [ProducesResponseType<CustomerDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<CustomerDto>> Get(CancellationToken cancellationToken)
    {
        var customerId = CurrentCustomerId();
        var customer = await database.Customers
            .AsNoTracking()
            .Include(item => item.Profile)
            .SingleOrDefaultAsync(item => item.Id == customerId, cancellationToken);
        if (customer is null)
        {
            return CustomerNotFoundProblem();
        }

        var hasPhoto = await database.CustomerPhotos
            .AsNoTracking()
            .AnyAsync(item => item.CustomerId == customerId, cancellationToken);
        return Ok(CustomerDto.From(customer, hasPhoto));
    }

    [HttpPut]
    [ServiceFilter(typeof(PersonalDataConsentFilter))]
    [ProducesResponseType<CustomerDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<CustomerDto>> Update(
        CustomerProfileUpdateRequest request,
        CancellationToken cancellationToken)
    {
        var customerId = CurrentCustomerId();
        var customer = await database.Customers
            .Include(item => item.Profile)
            .SingleOrDefaultAsync(item => item.Id == customerId, cancellationToken);
        if (customer is null)
        {
            return CustomerNotFoundProblem();
        }

        return await consents.WithPersonalDataAsync<ActionResult<CustomerDto>>(customerId, async () =>
        {
            Apply(customer.Profile, request);
            customer.State = CustomerProfileState.Evaluate(customer.Profile);
            customer.UpdatedAt = timeProvider.GetUtcNow();
            await database.SaveChangesAsync(cancellationToken);
            var hasPhoto = await database.CustomerPhotos
                .AsNoTracking()
                .AnyAsync(item => item.CustomerId == customerId, cancellationToken);
            return Ok(CustomerDto.From(customer, hasPhoto));
        }, cancellationToken);
    }

    [HttpGet("photo")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<ActionResult> GetPhoto(CancellationToken cancellationToken)
    {
        var customerId = CurrentCustomerId();
        var photo = await database.CustomerPhotos
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.CustomerId == customerId, cancellationToken);
        return photo is null
            ? PhotoNotFoundProblem()
            : File(photo.Content, photo.ContentType);
    }

    [HttpPut("photo")]
    [ServiceFilter(typeof(PersonalDataConsentFilter))]
    [Consumes("multipart/form-data")]
    [RequestSizeLimit(MaxPhotoSize + 64 * 1024)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<ActionResult> PutPhoto(IFormFile file, CancellationToken cancellationToken)
    {
        var customerId = CurrentCustomerId();
        return await consents.WithPersonalDataAsync<ActionResult>(customerId, async () =>
        {
            if (file.Length is <= 0 or > MaxPhotoSize)
            {
                return InvalidPhotoSizeProblem();
            }

            if (!PhotoSignatures.TryGetValue(file.ContentType, out var signatureValidator))
            {
                return InvalidPhotoTypeProblem();
            }

            await using var stream = file.OpenReadStream();
            await using var buffer = new MemoryStream((int)file.Length);
            await stream.CopyToAsync(buffer, cancellationToken);
            var content = buffer.ToArray();
            if (!signatureValidator(content))
            {
                return InvalidPhotoContentProblem();
            }

            if (!await database.Customers.AnyAsync(item => item.Id == customerId, cancellationToken))
            {
                return CustomerNotFoundProblem();
            }

            var photo = await database.CustomerPhotos
                .SingleOrDefaultAsync(item => item.CustomerId == customerId, cancellationToken);
            if (photo is null)
            {
                photo = new CustomerPhoto
                {
                    CustomerId = customerId,
                    FileName = SafeFileName(file.FileName),
                    ContentType = file.ContentType,
                    Content = content,
                    Size = content.Length,
                    UpdatedAt = timeProvider.GetUtcNow()
                };
                database.CustomerPhotos.Add(photo);
            }
            else
            {
                photo.FileName = SafeFileName(file.FileName);
                photo.ContentType = file.ContentType;
                photo.Content = content;
                photo.Size = content.Length;
                photo.UpdatedAt = timeProvider.GetUtcNow();
            }

            await database.SaveChangesAsync(cancellationToken);
            return NoContent();
        }, cancellationToken);
    }

    [HttpDelete("photo")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<ActionResult> DeletePhoto(CancellationToken cancellationToken)
    {
        var customerId = CurrentCustomerId();
        var photo = await database.CustomerPhotos
            .SingleOrDefaultAsync(item => item.CustomerId == customerId, cancellationToken);
        if (photo is not null)
        {
            database.CustomerPhotos.Remove(photo);
            await database.SaveChangesAsync(cancellationToken);
        }

        return NoContent();
    }

    private static void Apply(CustomerProfile profile, CustomerProfileUpdateRequest request)
    {
        profile.LastName = Normalize(request.LastName);
        profile.FirstName = Normalize(request.FirstName);
        profile.Patronymic = Normalize(request.Patronymic);
        profile.Email = Normalize(request.Email)?.ToLowerInvariant();
        profile.PassportSeries = Normalize(request.PassportSeries);
        profile.PassportNumber = Normalize(request.PassportNumber);
        profile.PassportIssueDate = request.PassportIssueDate;
        profile.PassportIssuedBy = Normalize(request.PassportIssuedBy);
        profile.Inn = Normalize(request.Inn);
        profile.PostalCode = Normalize(request.PostalCode);
        profile.City = Normalize(request.City);
        profile.Address = Normalize(request.Address);
    }

    private static string SafeFileName(string fileName)
    {
        var safe = Path.GetFileName(fileName.Trim());
        return string.IsNullOrWhiteSpace(safe) ? "photo" : safe[..Math.Min(safe.Length, 255)];
    }

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

}
