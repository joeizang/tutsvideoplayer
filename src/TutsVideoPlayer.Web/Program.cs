using System.Runtime.InteropServices;
using JsxCore;
using JsxCore.Hosting;
using JsxCore.Mvc;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using TutsVideoPlayer.Infrastructure.FileSystem;
using TutsVideoPlayer.Web.Features.Home;
using TutsVideoPlayer.Web.Features.Playback;
using TutsVideoPlayer.Web.Features.System;
using TutsVideoPlayer.Web.Hosting;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddProblemDetails(options =>
{
    options.CustomizeProblemDetails = context =>
    {
        context.ProblemDetails.Extensions.TryAdd("traceId", context.HttpContext.TraceIdentifier);
    };
});
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
builder.Services.AddHealthChecks().AddCheck<ApplicationReadinessCheck>("application", tags: ["ready"]);
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
}
