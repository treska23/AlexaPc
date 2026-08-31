using Android.App;
using Android.App.Admin;
using Android.Content;

namespace Bardo.Mobile;

[BroadcastReceiver(
    Name = "com.treska23.bardo.BardoDeviceAdminReceiver",
    Enabled = true,
    Exported = true,
    Permission = Android.Manifest.Permission.BindDeviceAdmin)]
[IntentFilter(["android.app.action.DEVICE_ADMIN_ENABLED"])]
[MetaData("android.app.device_admin", Resource = "@xml/bardo_device_admin")]
public sealed class BardoDeviceAdminReceiver : DeviceAdminReceiver
{
}
