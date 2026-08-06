# Contributing

## Requirements

- Windows 10 or Windows 11
- .NET 8 SDK
- Node.js 20 or later
- PowerShell 7 or Windows PowerShell 5.1

## Validation

Run before submitting a change:

```powershell
dotnet build VniteGameCache.sln -c Release
dotnet test VniteGameCache.sln -c Release --no-build

Push-Location src\VniteGameCache.Plugin
npm test
npm run build
Pop-Location
```

Changes that affect copying, cleanup, quarantine, saves, launch routing, or
Vnite takeover must include focused regression coverage. Do not use real game
libraries for destructive tests.
