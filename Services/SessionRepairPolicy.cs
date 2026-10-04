using Microsoft.Web.WebView2.Core;

namespace PokeQuad.Services;

public static class SessionRepairPolicy
{
    public const string ClearedCategoryDescription = "WebView2 DiskCache only";
    public static CoreWebView2BrowsingDataKinds BrowsingDataToClear => CoreWebView2BrowsingDataKinds.DiskCache;

    public static string ConfirmationTitle(int accountNumber) => $"Repair Account {accountNumber}?";

    public static string ConfirmationMessage =>
        "DRIFTR will restart this browser session and clear temporary browser cache for this session only.\n\n" +
        "Your other DRIFTR sessions will not be affected.\n\n" +
        "You should remain signed in where possible, but the website may require you to sign in again.";
}
