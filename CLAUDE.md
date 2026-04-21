# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Commands

Solution is `WebMudBlazorBasic.slnx` (two projects: `Library`, `Web`). Requires the .NET 10 SDK.

- Restore: `dotnet restore WebMudBlazorBasic.slnx`
- Build: `dotnet build WebMudBlazorBasic.slnx`
- Run the web app (listens on `http://localhost:8089`): `dotnet run --project Web/Web.csproj`
- Run with the Development profile: `dotnet run --project Web/Web.csproj --launch-profile "http devel"`
- Build container image: `docker build -f Web/Dockerfile -t webmudblazorbasic .` (build context must be the repo root because the Dockerfile copies both `Web/` and `Library/`).

There is no test project in the solution yet.

## Architecture

Server-interactive Blazor app (`.NET 10`, MudBlazor 9) with a shared `Library` for cross-cutting host/config helpers.

- `Web/Program.cs` is the single entry point and does **not** use the minimal top-level-statements shape — it is a classic `Program.Main`. Noteworthy bootstrap order:
  1. A Serilog *bootstrap* logger is created first so configuration loading itself can be logged.
  2. `IConfiguration` is built manually (`appsettings.json` + `appsettings.{DOTNET_ENVIRONMENT}.json` + env vars) and bound to the strongly-typed `Settings` record (`Web/Settings.cs`) **before** `WebApplication.CreateBuilder`. This `Settings` instance is then registered as a singleton.
  3. The real Serilog logger is reattached via `builder.Logging.ClearProviders()` + `AddSerilog` reading from `builder.Configuration`.
  4. `builder.AddOpenTelemetry()` (see `Library/Extensions.cs`) only wires Azure Monitor + OTLP if `APPLICATIONINSIGHTS_CONNECTION_STRING` is present — locally the app runs with no telemetry exporter.
  5. An `AzureOpenAIClient` is registered as a singleton using `Settings.AzureOpenAI` (endpoint + API key).
  6. `AddRazorComponents().AddInteractiveServerComponents()` + `AddMudServices()` wire MudBlazor and interactive Blazor Server rendering.
- Routing and rendering: `Components/App.razor` is the root document (loads MudBlazor CSS/JS, has `<ReconnectModal />` for Blazor Server reconnection). `Components/Routes.razor` points the router at `Layout.MainLayout` and `Pages.NotFound`. Pages live in `Web/Components/Pages/`; shared layout in `Web/Components/Layout/MainLayout.razor` (MudBlazor AppBar with a dark-mode toggle persisted in `localStorage`).
- Razor component `_Imports.razor` globally imports `MudBlazor`, `Web`, `Web.Components`, and `Web.Components.Layout` — new components don't need those `@using` lines.
- Diagnostics endpoints mapped from `Library.Extensions.MapDefaultEndpoints`: `GET /livez`, `GET /uptime`, `GET /error` (exception handler). `Program.cs` adds `GET /info` describing them. `UseStatusCodePagesWithReExecute("/not-found")` routes unknown paths through the `NotFound` page.
- The `Library` project intentionally has no runtime dependency on ASP.NET Core for its utility methods (`AllConfigurationKeys`, `OutputEnvironmentVariables`, `LogStrings`, `ToAzureBlobSafeName`) but does reference `Azure.Monitor.OpenTelemetry.AspNetCore` for the `AddOpenTelemetry` extension — keep that split in mind when adding helpers.

## Configuration

- `Web/appsettings.json` has a committed placeholder `Settings:AzureOpenAI:ApiKey = "dummy"`. Override via environment (`Settings__AzureOpenAI__ApiKey=...`) or an `appsettings.{env}.json` rather than editing the committed file.
- `Settings` is required — if the `Settings` section is missing or can't bind, `Program.Main` throws `InvalidOperationException` during startup.
- Kestrel is pinned to `http://*:8089` in `appsettings.json`; the Dockerfile and launch profiles assume the same port.

## Container notes

`Web/Dockerfile` uses the chiseled ASP.NET 10 runtime image and runs as the non-root `$APP_UID`. The `Container prod` launch profile in `Web/Properties/launchSettings.json` is what Visual Studio uses to run under Docker; it sets both `ASPNETCORE_ENVIRONMENT=Production` and `DOTNET_ENVIRONMENT=Production` (the latter matters because `Program.Main` reads `DOTNET_ENVIRONMENT`, not `ASPNETCORE_ENVIRONMENT`, to pick the per-environment `appsettings` file).
