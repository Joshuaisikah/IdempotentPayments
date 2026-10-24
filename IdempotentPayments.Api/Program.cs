using IdempotentPayments.Api.Endpoints;
using IdempotentPayments.Api.Services;
using IdempotentPayments.Infrastructure.Data;
using IdempotentPayments.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

//register the app  the database
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("paymentdb"))
);

builder.Services.AddSingleton<FakePaymentProvider>(
    new FakePaymentProvider { DefaultBehaviour = FakePaymentProvider.Behaviour.Success }
);
builder.Services.AddSingleton<IPaymentProvider>(sp => sp.GetRequiredService<FakePaymentProvider>());

// Scoped, because it holds the scoped AppDbContext
builder.Services.AddScoped<PaymentService>();
builder.Services.AddScoped<IdempotencyStore>();

var staleAfter = TimeSpan.FromSeconds(
    builder.Configuration.GetValue("Idempotency:StaleAfterSeconds", 90)
);
var providerTimeout = TimeSpan.FromSeconds(
    builder.Configuration.GetValue("Idempotency:ProviderTimeoutSeconds", 30)
);

var keyLifetime = TimeSpan.FromHours(
    builder.Configuration.GetValue("Idempotency:KeyLifetimeHours", 24)
);
builder.Services.AddSingleton(new IdempotencyOptions(staleAfter, providerTimeout, keyLifetime));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddScoped<PaymentSweep>();
builder.Services.AddHostedService<PaymentSweepService>();

// OpenAPI document generation (built-in since .NET 9, works on .NET 8+ with the package)
builder.Services.AddOpenApi();

var app = builder.Build();

// Development-only: serve the OpenAPI document + Scalar UI
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi(); // → /openapi/v1.json
    app.MapScalarApiReference(); // → /scalar
}

app.UseHttpsRedirection();
app.MapPaymentEndpoints();
app.Run();
