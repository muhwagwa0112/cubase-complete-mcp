namespace CubaseCompleteMcp.Core;

public sealed record CubasePaths(
    string RepositoryRoot,
    string LocalStateRoot,
    string BackupsRoot,
    string CubasePreferences,
    string MidiRemoteRoot,
    string DriverScriptsRoot,
    string LocalDriverRoot,
    string UserSettingsRoot,
    string ActiveDriverDirectory,
    string KeyCommandsFile)
{
    public static CubasePaths Discover(string? repositoryRoot = null)
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var midiRoot = Path.Combine(documents, "Steinberg", "Cubase", "MIDI Remote");
        var drivers = Path.Combine(midiRoot, "Driver Scripts");
        var cubasePreferences = Path.Combine(appData, "Steinberg", "Cubase 15_64");
        var state = Path.Combine(localAppData, "CubaseCompleteMCP");

        return new CubasePaths(
            Path.GetFullPath(repositoryRoot ?? Environment.CurrentDirectory),
            state,
            Path.Combine(state, "backups"),
            cubasePreferences,
            midiRoot,
            drivers,
            Path.Combine(drivers, "Local"),
            Path.Combine(drivers, "Local", CubaseConstants.DeviceVendor, CubaseConstants.DeviceName),
            Path.Combine(midiRoot, "User Settings"),
            Path.Combine(cubasePreferences, "Key Commands.xml"));
    }
}

