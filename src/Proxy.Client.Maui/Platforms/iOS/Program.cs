using System;
using System.IO;
using ObjCRuntime;
using UIKit;

namespace Proxy.Client.Maui;

public class Program
{
	static void Main(string[] args)
	{
		AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
		{
			RecordCrash(e.ExceptionObject as Exception);
		};

		System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (sender, e) =>
		{
			RecordCrash(e.Exception);
			e.SetObserved();
		};

		try
		{
			UIApplication.Main(args, null, typeof(AppDelegate));
		}
		catch (Exception ex)
		{
			RecordCrash(ex);
			throw;
		}
	}

	public static void RecordCrash(Exception? ex)
	{
		if (ex == null) return;
		try
		{
			string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
			string logPath = Path.Combine(docs, "crash.log");
			File.AppendAllText(logPath, $"[{DateTime.UtcNow:O}] CRASH: {ex}\n\n");
		}
		catch { }
	}
}
