using IdempotentPayments.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

//register the app  the database

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("paymentdb"))
);

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

app.Run();
