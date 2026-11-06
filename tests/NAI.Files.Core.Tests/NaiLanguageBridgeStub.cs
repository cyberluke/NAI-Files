// Test-harness stub for NaiLanguageBridge. The real bridge watches the NAI OS
// language settings file and needs app-only types (App.UiDispatcher,
// AppLanguageHelper). The core tests only exercise the observable stage hook
// used by NaiPnpDevices/NaiHardwareCache, so this stub keeps the harness
// compiling without dragging in the whole application surface.
namespace Files.App.Helpers
{
	internal static class NaiLanguageBridge
	{
		internal static void StartupStage(string stage) { }
	}
}