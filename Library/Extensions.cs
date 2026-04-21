using System.Collections;
using System.Text;
using System.Text.RegularExpressions;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Library
{
    public static class Extensions
    {
        public static List<string> AllConfigurationKeys(this IConfigurationRoot root)
        {
            (string? Value, IConfigurationProvider? Provider) GetValueAndProvider(IConfigurationRoot root, string key)
            {
                foreach (IConfigurationProvider provider in root.Providers.Reverse())
                {
                    if (provider.TryGet(key, out string? value))
                    {
                        return (value, provider);
                    }
                }

                return (null, null);
            }

            void RecurseChildren(HashSet<string> keys, IEnumerable<IConfigurationSection> children, string rootPath)
            {
                foreach (IConfigurationSection child in children)
                {
                    (string? Value, IConfigurationProvider? Provider) = GetValueAndProvider(root, child.Path);

                    if (Provider is not null && !String.IsNullOrEmpty(rootPath))
                    {
                        keys.Add(rootPath + ":" + child.Key + "=" + Value);
                    }

                    RecurseChildren(keys, child.GetChildren(), child.Path);
                }
            }

            var keys = new HashSet<string>();
            RecurseChildren(keys, root.GetChildren(), "");
            return keys.ToList();
        }

        public static void LogStrings(this List<string> list)
        {
            foreach (var e in list)
            {
                Serilog.Log.Information(e);
            }
        }

        public static void OutputEnvironmentVariables(this IDictionary d)
        {
            var envVars = d.Cast<DictionaryEntry>();
            envVars = envVars.OrderBy(x => (string)x.Key);

            foreach (var e in envVars)
            {
                Serilog.Log.Debug("{variabel}:{value}", e.Key, e.Value);
            }
        }

        /// <summary>
        /// Trims spaces and replaces invalid Azure Blob Storage name characters with underscores.
        /// </summary>
        /// <param name="input">The original string.</param>
        /// <returns>A sanitized blob name string.</returns>
        public static string ToAzureBlobSafeName(this string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return string.Empty;

            // Trim leading/trailing spaces
            var trimmed = input.Trim().Normalize(NormalizationForm.FormKC);

            // Azure blob names cannot contain: \, /, ?, #, and must not end with dot or slash
            // We'll also replace spaces and control characters
            var invalidCharsPattern = @"[\\\/\?\#\r\n\t\f\v ]";
            var sanitized = Regex.Replace(trimmed, invalidCharsPattern, "_");

            // Remove trailing dots or slashes
            sanitized = sanitized.TrimEnd('.', '/');

            var utf8Encoding = Encoding.GetEncoding(
                "US-ASCII",
                new EncoderReplacementFallback("_"),
                new DecoderReplacementFallback("_"));

            sanitized = utf8Encoding.GetString(utf8Encoding.GetBytes(sanitized));

            // Optionally, limit length to 1024 characters (Azure max)
            if (sanitized.Length > 1024)
                sanitized = sanitized.Substring(0, 1024);

            return sanitized;
        }

        public static IHostApplicationBuilder AddOpenTelemetry(this IHostApplicationBuilder builder)
        {
            // APPLICATIONINSIGHTS_CONNECTION_STRING injected from Aspire when running locally
            if (!string.IsNullOrEmpty(builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
            {
                builder.Services.AddOpenTelemetry()
                    .UseAzureMonitor()
                    .WithMetrics(metrics =>
                    {
                        metrics.AddAspNetCoreInstrumentation()
                        .AddHttpClientInstrumentation();
                    })
                    .WithTracing(traces =>
                    {
                        traces.AddAspNetCoreInstrumentation()
                        .AddHttpClientInstrumentation();
                    })
                    .ConfigureResource(resource => resource.AddService(serviceName: "worker"))
                    .UseOtlpExporter();
            }
            return builder;
        }

        public static WebApplication MapDefaultEndpoints(this WebApplication app, DateTimeOffset applicationStartTime)
        {            
            app.MapGet("/livez", () => "Live");
            app.MapGet("/uptime", () =>
            {
                var uptime = DateTimeOffset.UtcNow - applicationStartTime;
                return Results.Json(new
                {
                    startedAtUtc = applicationStartTime,
                    uptime = uptime.ToString("c"),
                    uptimeTotalSeconds = uptime.TotalSeconds,
                    uptimeDays = uptime.Days,
                    uptimeHours = uptime.Hours,
                    uptimeMinutes = uptime.Minutes,
                    uptimeSeconds = uptime.Seconds
                });
            });

            app.MapGet("/error", (HttpContext context) =>
            {
                var exceptionFeature = context.Features.Get<IExceptionHandlerFeature>();
                if (exceptionFeature?.Error is BadHttpRequestException badRequestException)
                {
                    // Handle BadHttpRequestException specifically
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    return Results.BadRequest(new { error = "Bad Request", details = badRequestException.Message });
                }
                // Handle other exceptions
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                return Results.Problem("An unexpected error occurred.");
            });

            return app;
        }
    }
}
