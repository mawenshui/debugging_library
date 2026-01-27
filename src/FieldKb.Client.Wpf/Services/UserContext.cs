namespace FieldKb.Client.Wpf;

public sealed class UserContext : IUserContext
{
    private string _currentUserName;
    private string _currentProfessionId;

    public UserContext(string initialUserName, string initialProfessionId)
    {
        _currentUserName = initialUserName;
        _currentProfessionId = ProfessionIds.Normalize(initialProfessionId);
    }

    public string CurrentUserName => _currentUserName;
    public string CurrentProfessionId => _currentProfessionId;

    public event EventHandler<string>? CurrentUserNameChanged;
    public event EventHandler<string>? CurrentProfessionIdChanged;

    public void SetCurrentUserName(string userName)
    {
        var normalized = UserNameRules.Normalize(userName);
        if (!UserNameRules.IsValid(normalized, out _))
        {
            return;
        }

        if (string.Equals(_currentUserName, normalized, StringComparison.Ordinal))
        {
            return;
        }

        _currentUserName = normalized;
        CurrentUserNameChanged?.Invoke(this, _currentUserName);
    }

    public void SetCurrentProfessionId(string professionId)
    {
        var normalized = ProfessionIds.Normalize(professionId);
        if (string.Equals(_currentProfessionId, normalized, StringComparison.Ordinal))
        {
            return;
        }

        _currentProfessionId = normalized;
        CurrentProfessionIdChanged?.Invoke(this, _currentProfessionId);
    }
}
