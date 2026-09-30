using AllInOne.Sdk;

namespace AllInOne.Core.Modules;

/// <summary>Что каркас (UI) предоставляет ядру: уведомления, диалоги, UI-поток.</summary>
public interface IHostUi
{
    void Notify(string title, string text);

    Task<ForceStopDecision> ConfirmForceStopAsync(string moduleName, string details);

    Task InvokeOnUiAsync(Action action);
}

public sealed class ModuleContext(ModuleManifest manifest, IHostUi ui, IHostServices host, HttpClient http) : IModuleContext
{
    public ModuleManifest Manifest { get; set; } = manifest;

    public string ModuleDir => AppPaths.ModuleDir(Manifest.Id);

    public string PayloadDir => Path.Combine(ModuleDir, "payload");

    public string DataDir
    {
        get
        {
            var dir = Path.Combine(ModuleDir, "data");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public string ManifestPath => Path.Combine(ModuleDir, "module.json");

    public bool IsInstalled => File.Exists(ManifestPath);

    public IModuleLog Log { get; } = Core.Log.For(manifest.Id);

    public HttpClient Http => http;

    public IHostServices Host => host;

    public void Notify(string title, string text) => ui.Notify(title, text);

    public Task<ForceStopDecision> ConfirmForceStopAsync(string moduleName, string details) => ui.ConfirmForceStopAsync(moduleName, details);

    /// <summary>Абсолютный путь внутри папки модуля.</summary>
    public string Resolve(string relative) => Path.GetFullPath(Path.Combine(ModuleDir, relative));
}
