using CommandLine;

namespace Uploader
{
    /// <summary>
    /// Command line options
    /// </summary>
    public class Options
    {
        [Option(HelpText = "Uses the last sent APK")]
        public bool UseLastApk { get; set; }

        [Option(HelpText = "APK path to upload")]
        public string Apk { get; set; }

        [Option(HelpText = "Path to the 'features' directoy with the tests. If not specified, it will be searched in the current directory")]
        public string FeaturesDir { get; set; }

        [Option(HelpText = "Device pool name to use")]
        public string DevicePool { get; set; }

        [Option(HelpText = "Test name. If not specified, a default value will be used")]
        public string TestName { get; set; }

        [Option(HelpText = "List all existing device pools")]
        public bool ListDevicePools { get; set; }

        [Option(HelpText = "Delete all finished runs")]
        public bool DeleteCompletedRuns { get; set; }

        [Option(HelpText = "Outputs to the console all logs")]
        public bool OutputLogs { get; set; }

        [Option(HelpText = "Directory to save the test assets")]
        public string ArtifactsSavePath { get; set; }
    }
}
