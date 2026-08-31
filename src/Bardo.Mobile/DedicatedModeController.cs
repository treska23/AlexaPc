using Android.App;
using Android.App.Admin;
using Android.Content;
using Android.OS;
using Android.Views;

namespace Bardo.Mobile;

internal static class DedicatedModeController
{
    internal static void ApplyWindow(Activity activity)
    {
        activity.Window?.AddFlags(
            WindowManagerFlags.ShowWhenLocked |
            WindowManagerFlags.DismissKeyguard);

        if (activity.Window?.DecorView is { } decorView)
        {
            decorView.SystemUiFlags =
                SystemUiFlags.ImmersiveSticky |
                SystemUiFlags.Fullscreen |
                SystemUiFlags.HideNavigation |
                SystemUiFlags.LayoutStable |
                SystemUiFlags.LayoutFullscreen |
                SystemUiFlags.LayoutHideNavigation;
        }
    }

    internal static void ApplyDeviceOwnerPolicies(Activity activity)
    {
        var manager = GetManager(activity);
        if (manager?.IsDeviceOwnerApp(activity.PackageName) != true)
        {
            return;
        }

        try
        {
            var admin = GetAdminComponent(activity);
            manager.SetLockTaskPackages(admin, [activity.PackageName!]);

            if (Build.VERSION.SdkInt >= BuildVersionCodes.P)
            {
                manager.SetLockTaskFeatures(admin, LockTaskFeatures.None);
            }

            manager.SetKeyguardDisabled(admin, true);

            var homeFilter = new IntentFilter(Intent.ActionMain);
            homeFilter.AddCategory(Intent.CategoryHome);
            homeFilter.AddCategory(Intent.CategoryDefault);
            var homeActivity = new ComponentName(
                activity,
                Java.Lang.Class.FromType(typeof(MainActivity)));
            manager.AddPersistentPreferredActivity(admin, homeFilter, homeActivity);

            if (manager.IsLockTaskPermitted(activity.PackageName))
            {
                var activityManager =
                    (ActivityManager?)activity.GetSystemService(Context.ActivityService);
                if (activityManager?.LockTaskModeState != LockTaskMode.Locked)
                {
                    activity.StartLockTask();
                }
            }
        }
        catch (Exception ex)
        {
            Android.Util.Log.Warn("BardoDedicated", $"No se pudo aplicar una política: {ex}");
        }
    }

    internal static string GetStatus(Context context)
    {
        var manager = GetManager(context);
        return manager?.IsDeviceOwnerApp(context.PackageName) == true
            ? "Modo dedicado: COMPLETO · propietario del dispositivo y quiosco"
            : "Modo dedicado: app persistente · falta aprovisionar como propietario";
    }

    internal static ComponentName GetAdminComponent(Context context) =>
        new(context, Java.Lang.Class.FromType(typeof(BardoDeviceAdminReceiver)));

    private static DevicePolicyManager? GetManager(Context context) =>
        (DevicePolicyManager?)context.GetSystemService(Context.DevicePolicyService);
}
