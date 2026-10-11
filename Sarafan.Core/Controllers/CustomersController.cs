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
    [Consumes("application/json")]
    [ServiceFilter(typeof(PersonalDataConsentFilter))]
    [ProducesResponseType<CustomerDto>(StatusCodes.Status200OK)]
    public Task<ActionResult<CustomerDto>> Update(CustomerProfileUpdateRequest request, CancellationToken cancellationToken)
        => SaveProfile(request, null, false, cancellationToken);

    [HttpPut("profile")]
    [Consumes("multipart/form-data")]
    [ServiceFilter(typeof(PersonalDataConsentFilter))]
    [RequestSizeLimit(MaxPhotoSize + 64 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxPhotoSize + 64 * 1024)]
    [ProducesResponseType<CustomerDto>(StatusCodes.Status200OK)]
    public Task<ActionResult<CustomerDto>> Save([FromForm] CustomerProfileSaveRequest request, CancellationToken cancellationToken)
        => SaveProfile(request, request.Photo, request.RemovePhoto, cancellationToken);

    private async Task<ActionResult<CustomerDto>> SaveProfile(
        CustomerProfileUpdateRequest request, IFormFile? file, bool removePhoto, CancellationToken cancellationToken)
    {
        if (file is not null && removePhoto) return ConflictingPhotoChangesProblem();
        var customerId = CurrentCustomerId();
        return await consents.WithPersonalDataAsync<ActionResult<CustomerDto>>(customerId, async () =>
        {
            await ConsentTransaction.LockCustomer(database, customerId, cancellationToken);
            var customer = await database.Customers.SingleOrDefaultAsync(item => item.Id == customerId, cancellationToken);
            if (customer is null) return CustomerNotFoundProblem();
            PhotoUpload? upload = null;
            if (file is not null)
            {
                var photoResult = await ReadPhoto(file, cancellationToken);
                if (photoResult.Result is not null) return photoResult.Result;
                upload = photoResult.Value!;
            }
            var now = timeProvider.GetUtcNow();
            if (upload is not null) await ApplyPhoto(customerId, upload, now, cancellationToken);
            else if (removePhoto) await RemovePhoto(customerId, cancellationToken);
            Apply(customer, request);
            customer.State = CustomerProfileState.Evaluate(customer);
            customer.UpdatedAt = now;
            await database.SaveChangesAsync(cancellationToken);
            var hasPhoto = await database.CustomerPhotos.AsNoTracking().AnyAsync(item => item.CustomerId == customerId, cancellationToken);
            return Ok(CustomerDto.From(customer, hasPhoto));
        }, cancellationToken);
    }

    [HttpPut("delivery-address")]
    [ServiceFilter(typeof(PersonalDataConsentFilter))]
    [ProducesResponseType<CustomerDto>(StatusCodes.Status200OK)]
    public Task<ActionResult<CustomerDto>> UpdateDeliveryAddress(CustomerDeliveryAddress request, CancellationToken cancellationToken)
    {
        var address = request.Normalize();
        var errors = address.Validate();
        if (errors.Count > 0) throw new ServiceException(400, "validation_failed") { Errors = errors };
        var customerId = CurrentCustomerId();
        return consents.WithPersonalDataAsync<ActionResult<CustomerDto>>(customerId, async () =>
        {
            await ConsentTransaction.LockCustomer(database, customerId, cancellationToken);
            var customer = await database.Customers.SingleOrDefaultAsync(item => item.Id == customerId, cancellationToken);
            if (customer is null) return CustomerNotFoundProblem();
            customer.PostalCode = address.PostalCode;
            customer.City = address.City;
            customer.Address = address.Address;
            customer.State = CustomerProfileState.Evaluate(customer);
            customer.UpdatedAt = timeProvider.GetUtcNow();
            await database.SaveChangesAsync(cancellationToken);
            var hasPhoto = await database.CustomerPhotos.AnyAsync(item => item.CustomerId == customerId, cancellationToken);
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
    [RequestFormLimits(MultipartBodyLengthLimit = MaxPhotoSize + 64 * 1024)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<ActionResult> PutPhoto(IFormFile file, CancellationToken cancellationToken)
    {
        var customerId = CurrentCustomerId();
        return await consents.WithPersonalDataAsync<ActionResult>(customerId, async () =>
        {
            var photoResult = await ReadPhoto(file, cancellationToken);
            if (photoResult.Result is not null) return photoResult.Result;
            var upload = photoResult.Value!;
            if (!await database.Customers.AnyAsync(item => item.Id == customerId, cancellationToken))
            {
                return CustomerNotFoundProblem();
            }

            await ApplyPhoto(customerId, upload, timeProvider.GetUtcNow(), cancellationToken);
            await database.SaveChangesAsync(cancellationToken);
            return NoContent();
        }, cancellationToken);
    }

    [HttpDelete("photo")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<ActionResult> DeletePhoto(CancellationToken cancellationToken)
    {
        await RemovePhoto(CurrentCustomerId(), cancellationToken);
        await database.SaveChangesAsync(cancellationToken);
        return NoContent();
    }

    private async Task RemovePhoto(int customerId, CancellationToken cancellationToken)
    {
        var photo = await database.CustomerPhotos.SingleOrDefaultAsync(item => item.CustomerId == customerId, cancellationToken);
        if (photo is not null) database.CustomerPhotos.Remove(photo);
    }

    private sealed record PhotoUpload(string FileName, string ContentType, byte[] Content);

    private async Task<ActionResult<PhotoUpload>> ReadPhoto(IFormFile file, CancellationToken cancellationToken)
    {
        if (file.Length is <= 0 or > MaxPhotoSize) return InvalidPhotoSizeProblem();
        if (!PhotoSignatures.TryGetValue(file.ContentType, out var signatureValidator))
            return InvalidPhotoTypeProblem();
        await using var stream = file.OpenReadStream();
        await using var buffer = new MemoryStream((int)file.Length);
        await stream.CopyToAsync(buffer, cancellationToken);
        var content = buffer.ToArray();
        if (!signatureValidator(content)) return InvalidPhotoContentProblem();
        return new PhotoUpload(SafeFileName(file.FileName), file.ContentType, content);
    }

    private async Task ApplyPhoto(int customerId, PhotoUpload upload, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var photo = await database.CustomerPhotos.SingleOrDefaultAsync(item => item.CustomerId == customerId, cancellationToken);
        if (photo is null)
        {
            photo = new CustomerPhoto { CustomerId = customerId, FileName = upload.FileName, ContentType = upload.ContentType, Content = upload.Content };
            database.CustomerPhotos.Add(photo);
        }
        photo.FileName = upload.FileName;
        photo.ContentType = upload.ContentType;
        photo.Content = upload.Content;
        photo.Size = upload.Content.Length;
        photo.UpdatedAt = now;
    }

    private static void Apply(Customer profile, CustomerProfileUpdateRequest request)
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
