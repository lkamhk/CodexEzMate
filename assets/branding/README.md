# Codex EzMate branding

- `codex-ezmate-app-icon.png`: symbol-only source for application, window, installer and tray icons.
- `codex-ezmate-logo-mate.png`: user-provided wordmark version for larger branding areas.
- `codex-ezmate-promo.png`: user-provided promotional artwork for the project README.
- `codex-ezmate-logo-concept-v2.png`: original generated concept retained as a source reference.

Regenerate the multi-resolution ICO and the small in-app wordmark preview from the project root:

```powershell
dotnet run --project tools/IconGenerator/IconGenerator.csproj -- src/CodexUsageAssistant.App/Assets/CodexUsageAssistant.ico
```

The generator preserves the supplied artwork and transparency while resizing. It does not generate a new design. The icon contains 16, 20, 24, 32, 40, 48, 64, 96, 128 and 256 pixel frames. The wordmark preview is limited to 256 pixels; the application decodes it at the required display size.

When preparing a source release, include this branding directory as well as the generated icon and preview under the application's Assets directory. README pages use the full-size wordmark and promotional artwork; application and tray icons use the symbol-only version.
