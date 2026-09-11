// Copyright (C) 2026 Maxim [maxirmx] Samsonov (www.sw.consulting)
// All rights reserved.
// This file is a part of the Sarafan application

using System.Text;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;

using Sarafan.Core;
using Sarafan.Core.Authentication;
using Sarafan.Core.Data;
using Sarafan.Core.Observability;
using Sarafan.Core.Services;

var builder = WebApplication.CreateBuilder(args);
var migrateOnly = args.Contains("--migrate-only", StringComparer.Ordinal);

SarafanObservability.Configure(builder);

var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("ConnectionStrings:DefaultConnection is required");
var authentication = builder.Configuration
    .GetSection(AuthenticationOptions.SectionName)
    .Get<AuthenticationOptions>()
    ?? throw new InvalidOperationException("Authentication configuration is required");
var backofficeAuthentication = builder.Configuration
    .GetSection(BackofficeAuthenticationOptions.SectionName)
    .Get<BackofficeAuthenticationOptions>()
    ?? throw new InvalidOperationException("BackofficeAuthentication configuration is required");
var backofficeBootstrap = builder.Configuration
    .GetSection(BackofficeBootstrapOptions.SectionName)
    .Get<BackofficeBootstrapOptions>()
    ?? new BackofficeBootstrapOptions();

authentication.Validate();
backofficeAuthentication.Validate();
backofficeBootstrap.Validate();
backofficeAuthentication.ValidateDistinctFrom(authentication);

builder.Services
    .AddOptions<AuthenticationOptions>()
    .Bind(builder.Configuration.GetSection(AuthenticationOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services
    .AddOptions<BackofficeAuthenticationOptions>()
    .Bind(builder.Configuration.GetSection(BackofficeAuthenticationOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();
builder.Services
    .AddOptions<BackofficeBootstrapOptions>()
    .Bind(builder.Configuration.GetSection(BackofficeBootstrapOptions.SectionName));
builder.Services.AddDbContext<AppDbContext>(options => options.UseNpgsql(connectionString));
builder.Services.AddSingleton<SarafanProblemDetailsFactory>();
builder.Services.AddExceptionHandler<SarafanExceptionHandler>();
builder.Services.AddProblemDetails();
builder.Services.AddControllers(options =>
{
    options.Filters.Add<ControllerLoggingFilter>(int.MinValue);
    options.Filters.Add<MandatoryCookieConsentFilter>();
});
builder.Services.Configure<ApiBehaviorOptions>(options =>
{
    options.SuppressMapClientErrors = true;
    options.InvalidModelStateResponseFactory = context => context.HttpContext
        .RequestServices
        .GetRequiredService<SarafanProblemDetailsFactory>()
        .CreateValidationResult(context.HttpContext, context.ModelState);
});
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<VerificationAttemptStore>();
builder.Services.AddSingleton<IPhoneNormalizer, PhoneNormalizer>();
builder.Services.AddSingleton<IVerificationCodeProvider, PhoneSuffixVerificationCodeProvider>();
builder.Services.AddSingleton<VerificationCodeReleaseGate>();
builder.Services.AddScoped<JwtTokenService>();
builder.Services.AddScoped<AuthenticationService>();
builder.Services.AddOptions<ConsentOptions>().Bind(builder.Configuration.GetSection(ConsentOptions.SectionName))
    .ValidateDataAnnotations().ValidateOnStart();
builder.Services.AddScoped<LegalDocumentService>();
builder.Services.AddScoped<ConsentService>();
builder.Services.AddScoped<MandatoryCookieConsentFilter>();
builder.Services.AddScoped<PersonalDataConsentFilter>();
builder.Services.AddScoped<ConsentWithdrawalRequestService>();
builder.Services.AddScoped<ConsentRetentionService>();
if (builder.Configuration.GetValue("Consents:RetentionWorkerEnabled", true))
    builder.Services.AddHostedService<ConsentRetentionWorker>();
builder.Services.AddScoped<IBackofficePasswordHasher, BCryptBackofficePasswordHasher>();
builder.Services.AddScoped<BackofficeJwtTokenService>();
builder.Services.AddScoped<BackofficeAuthenticationService>();
builder.Services.AddScoped<BackofficeUserService>();
builder.Services.AddScoped<BackofficeBootstrapService>();
builder.Services.AddScoped<BackofficeJwtBearerEvents>();
builder.Services.AddScoped<Microsoft.AspNetCore.Authorization.IAuthorizationHandler, CustomerAccessHandler>();
builder.Services.AddHttpClient<ICbrRateClient, CbrRateClient>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
    client.MaxResponseContentBufferSize = 1_048_576;
});
builder.Services.AddScoped<ExchangeRateService>();
builder.Services.AddScoped<IExchangeRateSynchronizer>(services => services.GetRequiredService<ExchangeRateService>());
if (builder.Configuration.GetValue("ExchangeRates:Enabled", true))
{
    builder.Services.AddHostedService<ExchangeRateWorker>();
}

var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(authentication.SigningKey));
var backofficeSigningKey = new SymmetricSecurityKey(
    Encoding.UTF8.GetBytes(backofficeAuthentication.SigningKey));
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false;
        options.TokenValidationParameters = JwtTokenService.CreateValidationParameters(authentication, signingKey);
        options.Events = new SarafanJwtBearerEvents();
    })
    .AddJwtBearer(BackofficeAuthenticationDefaults.Scheme, options =>
    {
        options.MapInboundClaims = false;
        options.Challenge = "Bearer";
        options.TokenValidationParameters = BackofficeJwtTokenService.CreateValidationParameters(
            backofficeAuthentication,
            backofficeSigningKey);
        options.EventsType = typeof(BackofficeJwtBearerEvents);
    });
builder.Services.AddAuthorization(options =>
{
    CustomerAuthorization.Configure(options);
    BackofficeAuthorization.Configure(options);
});
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo { Title = "Sarafan Core Api", Version = "v1" });
    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Description = "JWT Authorization token. Example: \"Authorization: Bearer {token}\"",
        Name = "Authorization",
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.Http,
        Scheme = "bearer"
    });
    options.AddSecurityDefinition("BackofficeBearer", new OpenApiSecurityScheme
    {
        Description = "Back-office JWT Authorization token. Example: \"Authorization: Bearer {token}\"",
        Name = "Authorization",
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.Http,
        Scheme = "bearer"
    });
    options.AddSecurityRequirement(document => new OpenApiSecurityRequirement
    {
        [new OpenApiSecuritySchemeReference("Bearer", document)] = []
    });
});
builder.Services.Configure<ForwardedHeadersOptions>(options =>
    ForwardedHeadersConfiguration.Configure(options, builder.Configuration));

var app = builder.Build();
var applicationLogger = app.Services.GetRequiredService<ILogger<ApplicationLifecycle>>();

app.Services.GetRequiredService<VerificationCodeReleaseGate>().EnsureAllowed();

if (migrateOnly || builder.Configuration.GetValue<bool>("Database:ApplyMigrations"))
{
    SarafanEvents.MigrationStarted(applicationLogger);
    try
    {
        await using var scope = app.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await database.Database.MigrateAsync();
        await scope.ServiceProvider
            .GetRequiredService<BackofficeBootstrapService>()
            .ProvisionAsync(CancellationToken.None);
        SarafanEvents.MigrationCompleted(applicationLogger);
    }
    catch (Exception exception)
    {
        SarafanEvents.MigrationFailed(applicationLogger, exception);
        throw;
    }
}

await using (var scope = app.Services.CreateAsyncScope())
{
    await scope.ServiceProvider
        .GetRequiredService<BackofficeBootstrapService>()
        .EnsureReleaseGateAsync(CancellationToken.None);
}

if (migrateOnly)
{
    return;
}

app.UseForwardedHeaders();
app.UseMiddleware<RequestLoggingMiddleware>();
app.UseExceptionHandler();
app.UseStatusCodePages(async context =>
{
    var httpContext = context.HttpContext;
    var factory = httpContext.RequestServices.GetRequiredService<SarafanProblemDetailsFactory>();
    await factory.WriteAsync(
        httpContext,
        httpContext.Response.StatusCode,
        SarafanProblemDetailsFactory.CodeForStatus(httpContext.Response.StatusCode),
        httpContext.RequestAborted);
});
app.UseAuthentication();
app.UseSwagger();
app.UseSwaggerUI();
app.UseAuthorization();

app.MapGet("/", () => Results.Redirect("/api/v1/status/status"));

app.MapControllers();

app.Lifetime.ApplicationStarted.Register(() => SarafanEvents.ApplicationStarted(
    applicationLogger,
    VersionInfo.AppVersion,
    app.Environment.EnvironmentName));
app.Lifetime.ApplicationStopped.Register(() => SarafanEvents.ApplicationStopped(applicationLogger));

app.Run();

public partial class Program;
