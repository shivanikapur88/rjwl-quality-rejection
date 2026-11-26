// Recovered by decompiling the supplied Quality.Mobile.Core.dll (Debug build, 23 Oct 2026); the original source was not in the package.
using System;

namespace Quality.Mobile.Core.Services;

public static class BaseUrlValidator
{
	public static bool TryNormalize(string? input, out string normalized, out string error)
	{
		normalized = "";
		error = "";
		string text = (input ?? "").Trim();
		if (text.Length == 0)
		{
			error = "Enter the server address.";
			return false;
		}
		if (!Uri.TryCreate(text, UriKind.Absolute, out Uri result) || (result.Scheme != Uri.UriSchemeHttps && result.Scheme != Uri.UriSchemeHttp))
		{
			error = "Enter a full address such as https://server:port";
			return false;
		}
		if (result.Scheme != Uri.UriSchemeHttps)
		{
			error = "The server address must start with https://";
			return false;
		}
		if (!string.IsNullOrEmpty(result.Query) || !string.IsNullOrEmpty(result.Fragment) || !string.IsNullOrEmpty(result.UserInfo))
		{
			error = "The server address must not contain a query, fragment or credentials.";
			return false;
		}
		normalized = result.GetLeftPart(UriPartial.Authority) + result.AbsolutePath.TrimEnd('/');
		return true;
	}
}
