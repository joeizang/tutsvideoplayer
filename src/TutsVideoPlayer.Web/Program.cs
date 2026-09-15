using System.Runtime.InteropServices;
using JsxCore;
using JsxCore.Hosting;
using JsxCore.Mvc;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TutsVideoPlayer.Infrastructure.Persistence;
using TutsVideoPlayer.Web.Features.Home;
using TutsVideoPlayer.Web.Features.Library;
using TutsVideoPlayer.Web.Features.Playback;
using TutsVideoPlayer.Web.Features.Preparation;
using TutsVideoPlayer.Web.Features.Watch;
using TutsVideoPlayer.Web.Hosting;
using TutsVideoPlayer.Infrastructure.FileSystem;
using TutsVideoPlayer.Web.Features.System;
using TutsVideoPlayer.Web.Persistence;

var builder = WebApplication.CreateBuilder(args);

if (args.Any(arg => arg == "migrate"))
{
    await MigrateAndExitAsync(builder);
    return;
}

builder.Services.AddControllers(mvcOptions =>
{
    // Applied to every controller so a future mutating endpoint is protected by default.
    mvcOptions.Filters.Add<SameOriginMutationFilter>();
});
builder.Services.AddProblemDetails(options =>
{
    options.CustomizeProblemDetails = context =>
    {
        context.ProblemDetails.Extensions.TryAdd("traceId", context.HttpContext.TraceIdentifier);
    };
});
builder.Services
    .AddOptions<AppOptions>()
    .BindConfiguration(AppOptions.SectionName)
    .Validate(options => !string.IsNullOrWhiteSpace(options.LibraryRoot), "App:LibraryRoot must be configured.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.DataDirectory), "App:DataDirectory must be configured.")
    .ValidateOnStart();
builder.Services
    .AddOptions<MediaDeliveryOptions>()
    .BindConfiguration(MediaDeliveryOptions.SectionName)
    .Validate(options => !string.IsNullOrWhiteSpace(options.FixtureDirectory))
    .ValidateOnStart();
builder.Services.AddSingleton<MediaFileLocator>(services =>
{
    var options = services.GetRequiredService<IOptions<MediaDeliveryOptions>>().Value;
    var root = Path.IsPathRooted(options.FixtureDirectory)
        ? options.FixtureDirectory
        : Path.Combine(builder.Environment.ContentRootPath, options.FixtureDirectory);
    return new MediaFileLocator(root);
});
builder.Services.AddSingleton<SystemInfoService>();
builder.Services.AddSingleton<SchemaReadiness>();
var appOptions = builder.Configuration.GetSection(AppOptions.SectionName).Get<AppOptions>() ?? new AppOptions();
var preparationOptions = builder.Configuration.GetSection(PreparationOptions.SectionName).Get<PreparationOptions>() ?? new PreparationOptions();
builder.Services.AddCatalog(appOptions, preparationOptions);
builder.Services.AddHealthChecks()
    .AddCheck<DatabaseReadinessCheck>("database", tags: ["ready"]);
builder.AddJsxCore();

var app = builder.Build();

app.UseJsxCore();
app.UseStaticFiles();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseRouting();
app.MapControllers();
app.MapHealthChecks("/health/live");
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = registration => registration.Tags.Contains("ready"),
    ResponseWriter = async (context, report) =>
    {
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(new
        {
            status = report.Status.ToString(),
            data = report.Entries.ToDictionary(
                entry => entry.Key,
                entry => new { status = entry.Value.Status.ToString(), entry.Value.Data })
        }, context.RequestAborted);
    }
});

app.Run();

public partial class Program
{

    internal static string DescribeRuntime() => RuntimeInformation.FrameworkDescription;

    private static async Task MigrateAndExitAsync(WebApplicationBuilder builder)
    {
        builder.Services.AddCatalog(
            builder.Configuration.GetSection(AppOptions.SectionName).Get<AppOptions>() ?? new AppOptions(),
            builder.Configuration.GetSection(PreparationOptions.SectionName).Get<PreparationOptions>() ?? new PreparationOptions());
        using var app = builder.Build();
        using var scope = app.Services.CreateScope();
        var initializer = scope.ServiceProvider.GetRequiredService<DatabaseInitializer>();
        await initializer.MigrateAsync();
        Console.WriteLine("Database migration complete.");
    }
}
