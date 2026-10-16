using System;
using Foundation;
using UIKit;

namespace Proxy.Client.Maui;

[Register("AppDelegate")]
public class AppDelegate : MauiUIApplicationDelegate
{
	protected override MauiApp CreateMauiApp()
	{
		try
		{
			return MauiProgram.CreateMauiApp();
		}
		catch (Exception ex)
		{
			Program.RecordCrash(ex);
			throw;
		}
	}

	public override bool FinishedLaunching(UIApplication application, NSDictionary launchOptions)
	{
		try
		{
			return base.FinishedLaunching(application, launchOptions);
		}
		catch (Exception ex)
		{
			Program.RecordCrash(ex);
			throw;
		}
	}
}
