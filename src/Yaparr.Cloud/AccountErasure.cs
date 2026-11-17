namespace Yaparr.Cloud;

/// <summary>
/// Removes an account and everything the service holds for it: the account (and with it every signed-in device), its backup, the devices registered to be woken
/// and the synced records. Used by the person deleting their own account (<c>/account/delete</c>), by the admin portal and by <c>accounts remove</c>, so that none
/// of them leaves part of an account behind.
/// </summary>
public static class AccountErasure
{
    /// <returns>Whether there was such an account, and whether it had a backup (false too when the backup was kept).</returns>
    public static async Task<(bool Existed, bool HadBackup)> EraseAsync(string name, IAccounts accounts, IBackupStore backups, IPushRegistry push, ISyncStore sync,
        bool keepBackup = false, CancellationToken cancel = default)
    {
        // The account first: from then on no token of it works, so nothing can be written to it while the rest goes.
        if (!accounts.Remove(name)) return (false, false);
        push.RemoveAccount(name);
        sync.RemoveAccount(name);
        var hadBackup = !keepBackup && await backups.DeleteAsync(BackupStore.FolderOf(name), cancel);
        return (true, hadBackup);
    }
}
