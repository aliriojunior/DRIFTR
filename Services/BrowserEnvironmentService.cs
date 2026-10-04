using System.IO;
using Microsoft.Web.WebView2.Core;
using PokeQuad.Config;

namespace PokeQuad.Services;

public static class BrowserEnvironmentService
{
    public static async Task<CoreWebView2Environment> CreateForAccountAsync(int accountNumber)
    {
        string profileDirectory = AppConfig.GetProfileDirectory(accountNumber);
        Directory.CreateDirectory(profileDirectory);

        // A distinct environment and user-data folder are intentionally created for every account.
        return await CoreWebView2Environment.CreateAsync(userDataFolder: profileDirectory);
    }
}
