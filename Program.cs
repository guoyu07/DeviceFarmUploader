using System;
using System.Linq;
using Amazon.DeviceFarm;
using Amazon.DeviceFarm.Model;
using Amazon;
using System.IO;
using System.Net;
using System.Diagnostics;
using System.Threading;
using CommandLine;
using System.IO.Compression;
using System.Collections.Generic;

namespace Uploader
{
	/// <summary>
	/// Reference
	///		- https://mobile.awsblog.com/post/TxROO0QM0WSCJX/Get-started-with-the-AWS-Device-Farm-CLI-and-Calabash-Part-1-Creating-a-Device-F
	///		- http://docs.aws.amazon.com/devicefarm/latest/APIReference/Welcome.html
	/// </summary>
	public class Program
	{
		private AmazonDeviceFarmClient client;
		private const string ProjectArn = "arn:aws:devicefarm:us-west-2:XXXXXXXXXXXXX:project:YYYYYYYYYYYY";
		private const string AwsAccessKey = "XXXXXXXXXX";
		private const string AwsSecretKey = "XXXXXXXXXX";
		private string runArn;
		private Options options;

		public static void Main(string[] args)
		{
			var p = new Program();
			p.Run(args);
		}

		private void UploadFile(string localPath, string url)
		{
			using (var fs = new FileStream(localPath, FileMode.Open, FileAccess.Read))
			{
				var httpUploadRequest = WebRequest.Create(url) as HttpWebRequest;
				httpUploadRequest.Method = "PUT";
				httpUploadRequest.ContentLength = fs.Length;
				httpUploadRequest.AllowWriteStreamBuffering = false;
				httpUploadRequest.Timeout = 99999;

				var buffer = new byte[16 * 1024];
				var bytesRead = 0;
				var sw = new Stopwatch();
				sw.Start();
				
				using (var dataStream = httpUploadRequest.GetRequestStream())
				{
					var total = 0;
					while ((bytesRead = fs.Read(buffer, 0, buffer.Length)) > 0)
					{
						total += bytesRead;
						var apkProgress = $"{(int)((double)total / fs.Length * 100)}%";

						if (options.OutputLogs)
						{
							ReportProgress($"Sending APK: {apkProgress}");
						}
						else
						{
							Console.Write($"\r{apkProgress}             ");
						}

						dataStream.Write(buffer, 0, bytesRead);
					}
				}

				sw.Stop();
				Console.WriteLine($"\nSent {fs.Length} bytes in {(int)(sw.ElapsedMilliseconds / 1000)}s");
			}
		}

		private UploadStatus GetUploadStatus(string arn)
		{
			GetUploadResponse response;
			UploadStatus status;
			do
			{
				response = client.GetUpload(arn);
				status = response.Upload.Status;
				Thread.Sleep(1000);
			}
			while (status != UploadStatus.FAILED && status != UploadStatus.SUCCEEDED);

			return status;
		}

		private void Run(string[] args)
		{
			Parser.Default.ParseArguments<Options>(args).WithParsed(Run);
		}

		private string FindDevicePoolByName(string name)
		{
			var result = client.ListDevicePools(new ListDevicePoolsRequest { Arn = ProjectArn });
			var devicePool = result.DevicePools.FirstOrDefault(dp => dp.Name == name);

			if (devicePool == null)
			{
				var msg = $"DevicePool '{name}' not found";
				ReportError(msg);
				throw new Exception(msg);
			}

			return devicePool.Arn;
		}

		private string GetLatestAppArn()
		{
			var result = client.ListUploads(new ListUploadsRequest { Arn = ProjectArn })
				.Uploads
				.Where(u => u.Type == UploadType.ANDROID_APP)
				.OrderByDescending(u => u.Created)
				.FirstOrDefault();

			Console.WriteLine($"Using app '{result.Name}', created at {result.Created}");

			return result.Arn;
		}

		private string UploadApk(string apkPath)
		{
			var createUploadResponse = client.CreateUpload(new CreateUploadRequest
			{
				ProjectArn = ProjectArn,
				Name = "android.apk",
				Type = UploadType.ANDROID_APP
			});

			// Send the APK
			ReportProgress("Sending APK...");
			UploadFile(apkPath, createUploadResponse.Upload.Url);

            // Check upload status
			var appArn = createUploadResponse.Upload.Arn;
			UploadStatus uploadStatus = GetUploadStatus(appArn);

			return uploadStatus == UploadStatus.SUCCEEDED ? appArn : null;
		}

		private void ListDevicePools()
		{
			var pools = client.ListDevicePools(new ListDevicePoolsRequest { Arn = ProjectArn });

			foreach (var p in pools.DevicePools)
			{
				Console.WriteLine($"- '{p.Name}'\n\t{p.Description}\n");
			}
		}

		private void DeleteCompletedRuns()
		{
			var runs = client.ListRuns(new ListRunsRequest { Arn = ProjectArn });

			foreach (var run in runs.Runs)
			{
				client.DeleteRun(new DeleteRunRequest { Arn = run.Arn });
			}
		}

		private void DownloadAllArtifacts()
		{
			if (string.IsNullOrEmpty(options.ArtifactsSavePath))
			{
				return;
			}

			if (string.IsNullOrEmpty(runArn))
			{
				var response = client.ListRuns(new ListRunsRequest { Arn = ProjectArn });
				runArn = response.Runs.First().Arn;
			}

			var testPaths = CreateArtifactsDirectoryStructure();

			DownloadArtifacts(ArtifactCategory.SCREENSHOT, testPaths);
			DownloadArtifacts(ArtifactCategory.FILE, testPaths);
			DownloadArtifacts(ArtifactCategory.LOG, testPaths);
		}

		private void DownloadArtifacts(ArtifactCategory category, Dictionary<string, string> references)
		{
			var response = client.ListArtifacts(new ListArtifactsRequest { Arn = runArn, Type = category });
			var downloaded = 0;

			foreach (var item in response.Artifacts)
			{
				ReportProgress($"Downloading artifact {category.Value} {++downloaded}/{response.Artifacts.Count}");

				var downloadRequest = WebRequest.Create(item.Url) as HttpWebRequest;
				downloadRequest.Method = "GET";
				downloadRequest.AllowReadStreamBuffering = false;
				downloadRequest.Timeout = 9999;

				var p = item.Arn.Split('/');
				var key = $"{p[2]}/{p[3]}/{p[4]}";
				var testPath = references[key];
				var filename = Path.Combine(testPath, $"{SanitizeFilename(item.Name)}.{item.Extension}");

				using (var outStream = new FileStream(filename, FileMode.Create, FileAccess.Write))
				{
					var buffer = new byte[16 * 1024];
					int c;

					using (var inStream = downloadRequest.GetResponse().GetResponseStream())
					{
						while ((c = inStream.Read(buffer, 0, buffer.Length)) > 0)
						{
							outStream.Write(buffer, 0, c);
						}
					}
				}
			}
		}

		private string SanitizeFilename(string s)
		{
			var invalids = Path.GetInvalidFileNameChars();
			return string.Join("_", s.Split(invalids, StringSplitOptions.RemoveEmptyEntries)).TrimEnd('.');
		}

		private Dictionary<string, string> CreateArtifactsDirectoryStructure()
		{
			var structure = new Dictionary<string, string>();

			// Jobs
			var jobList = client.ListJobs(new ListJobsRequest { Arn = runArn });

			foreach (var job in jobList.Jobs)
			{
				var jobPath = SanitizeFilename(job.Name);
				var jobId = ExtractArnId(job.Arn);

				// Suites
				var suiteList = client.ListSuites(new ListSuitesRequest { Arn = job.Arn });

				foreach (var suite in suiteList.Suites)
				{
					var suiteId = ExtractArnId(suite.Arn);
					var suitePath = SanitizeFilename(suite.Name);

					// Tests
					var testList = client.ListTests(new ListTestsRequest { Arn = suite.Arn });

					foreach (var test in testList.Tests)
					{
						var testId = ExtractArnId(test.Arn);
						var testPath = Path.Combine(options.ArtifactsSavePath, jobPath, suitePath, SanitizeFilename(test.Name));
						Directory.CreateDirectory(testPath);
						structure[$"{jobId}/{suiteId}/{testId}"] = testPath;
					}
				}
			}

			return structure;
		}

		private string ExtractArnId(string arn)
		{
			return arn.Split('/').Last();
		}

		private void Run(Options options)
		{
			this.options = options;
			client = new AmazonDeviceFarmClient(AwsAccessKey, AwsSecretKey, RegionEndpoint.USWest2);

			if (options.ListDevicePools)
			{
				ListDevicePools();
				return;
			}

			if (options.DeleteCompletedRuns)
			{
				DeleteCompletedRuns();
				return;
			}

			var devicePoolArn = FindDevicePoolByName(options.DevicePool);

			if (string.IsNullOrEmpty(devicePoolArn))
			{
				ReportError($"Error: device pool '{options.DevicePool}' not found");
				return;
			}

			if (string.IsNullOrEmpty(options.FeaturesDir))
			{
				options.FeaturesDir = Path.Combine(Environment.CurrentDirectory, "features");
			}

			if (!Directory.Exists(options.FeaturesDir))
			{
				ReportError($"Directory '{options.FeaturesDir}' not found");
				return;
			}

			if (!options.UseLastApk && string.IsNullOrEmpty(options.Apk))
			{
				ReportError("You need to specifcy the package to use");
				return;
			}

			var appArn = options.UseLastApk
				? GetLatestAppArn()
				: UploadApk(options.Apk);

			if (string.IsNullOrEmpty(appArn))
			{
				ReportError($"Package not found or failed to upload");
				return;
			}

			var testServersPath = Path.Combine(Environment.CurrentDirectory, "test_servers");

			if (Directory.Exists(testServersPath))
			{
				Directory.Delete(testServersPath, true);
			}

			var featuresZip = Path.Combine(Path.GetDirectoryName(options.FeaturesDir), "features.zip");
			File.Delete(featuresZip);
			ZipFile.CreateFromDirectory(options.FeaturesDir, featuresZip, CompressionLevel.Fastest, true);

			// Upload the tests
			var createUploadResponse = client.CreateUpload(new CreateUploadRequest
			{
				ProjectArn = ProjectArn,
				Name = "features.zip",
				Type = UploadType.CALABASH_TEST_PACKAGE
			});

			Console.WriteLine("Sending features.zip");
			UploadFile(featuresZip, createUploadResponse.Upload.Url);

			var testPackageArn = createUploadResponse.Upload.Arn;
			var uploadStatus = GetUploadStatus(testPackageArn);

			if (uploadStatus == UploadStatus.FAILED)
			{
				ReportError("Failed to upload features.zip");
				return;
			}

            // Schedule test run
			var scheduleResponse = client.ScheduleRun(new ScheduleRunRequest
			{
				ProjectArn = ProjectArn,
				AppArn = appArn,
				DevicePoolArn = devicePoolArn,
				Name = options.TestName ?? $"Run {DateTime.Now.ToString()}",
				Test = new ScheduleRunTest
				{
					Type = TestType.CALABASH,
					TestPackageArn = testPackageArn
				},
				Configuration = new ScheduleRunConfiguration
				{
					Radios = new Radios { Wifi = true },
					Locale = "en_US",
				}
			});

			GetRunResponse runResponse;
			ReportProgress("Waiting test to finish (it takes a long time, please be patient)");

			do
			{
				Console.Write(".");
				runArn = scheduleResponse.Run.Arn;
				runResponse = client.GetRun(runArn);
				Thread.Sleep(3000);
			}
			while (runResponse.Run.Status != ExecutionStatus.COMPLETED);

			if (runResponse.Run.Result != ExecutionResult.PASSED)
			{
				ReportError("Test run failed");
				return;
			}

			DownloadAllArtifacts();
			ReportProgress("Test run finished successfully");
		}

		private void ReportError(string msg)
		{
			Console.WriteLine(msg);

			if (options.OutputLogs)
			{
				Console.WriteLine("##teamcity[buildProblem description='{0}']", msg);
			}
		}

		private void ReportProgress(string msg)
		{
			Console.WriteLine(msg);

			if (options.OutputLogs)
			{
				Console.WriteLine("##teamcity[progressMessage '{0}']", msg);
			}
		}
	}
}
