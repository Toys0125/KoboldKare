using System;

public static class BasisServerBrowserRefresh {
    public static event Action Requested;

    public static void RequestRefresh() {
        Requested?.Invoke();
    }
}
