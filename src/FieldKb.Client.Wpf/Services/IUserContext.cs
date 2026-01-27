namespace FieldKb.Client.Wpf;

public interface IUserContext
{
    string CurrentUserName { get; }

    event EventHandler<string> CurrentUserNameChanged;

    void SetCurrentUserName(string userName);

    string CurrentProfessionId { get; }

    event EventHandler<string> CurrentProfessionIdChanged;

    void SetCurrentProfessionId(string professionId);
}
