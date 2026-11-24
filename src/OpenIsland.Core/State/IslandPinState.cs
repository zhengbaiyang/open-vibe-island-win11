namespace OpenIsland.Core.State;

/// <summary>
/// Tracks whether the Dynamic Island's expanded state is pinned manually by the user
/// or auto-pinned solely because an attention request (approval / question) popped up.
/// </summary>
public sealed class IslandPinState
{
    public bool IsPinned { get; private set; }
    public bool IsAutoPinnedForAttention { get; private set; }

    public void PinManually()
    {
        IsPinned = true;
        IsAutoPinnedForAttention = false;
    }

    public void Unpin()
    {
        IsPinned = false;
        IsAutoPinnedForAttention = false;
    }

    public void ToggleManualPin()
    {
        IsPinned = !IsPinned;
        IsAutoPinnedForAttention = false;
    }

    public void OnAttentionNotification()
    {
        if (!IsPinned)
        {
            IsPinned = true;
            IsAutoPinnedForAttention = true;
        }
    }

    /// <summary>
    /// Releases an attention-triggered auto-pin once no pending approval or question remains.
    /// Manual pins are never cleared automatically.
    /// </summary>
    public bool ReleaseAutoPinIfNoAttention(bool hasPendingAttention)
    {
        if (!hasPendingAttention && IsAutoPinnedForAttention)
        {
            IsAutoPinnedForAttention = false;
            IsPinned = false;
            return true;
        }

        return false;
    }
}
