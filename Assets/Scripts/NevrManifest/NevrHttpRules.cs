// The only answer the installer takes from the update servers is a plain 200. A redirect is
// never followed: it could send the request to http, or to a host that is not pinned, and
// the clock for the manifest must come from the original https response.
public static class NevrHttpRules
{
    public static string RefusalFor(long responseCode)
    {
        if (responseCode == 200)
            return null;

        if (responseCode >= 300 && responseCode < 400)
            return "The update server tried to send the installer to another address, which the installer does not follow (HTTP " + responseCode + ").";

        return "The update server did not answer the way the installer expects (HTTP " + responseCode + ").";
    }
}
