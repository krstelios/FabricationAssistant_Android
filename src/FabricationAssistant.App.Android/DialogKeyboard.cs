using Android.Views;
using Android.Views.InputMethods;
using Android.Widget;

namespace FabricationAssistant.App.Android;

internal static class DialogKeyboard
{
    public static void ConfirmOnEnter(EditText? input, View? confirmView)
    {
        if (input is null || confirmView is null)
            return;

        input.ImeOptions = ImeAction.Done;
        input.SetOnEditorActionListener(new ConfirmOnEnterListener(() => TryPerformClick(confirmView)));
    }

    /// <summary>
    /// Runs <paramref name="confirm"/> when the user presses Enter/Done in
    /// <paramref name="input"/>. For fields that commit a value directly rather
    /// than clicking a button (e.g. numeric settings fields).
    /// </summary>
    public static void ConfirmOnEnter(EditText? input, Action confirm)
    {
        if (input is null)
            return;

        input.ImeOptions = ImeAction.Done;
        input.SetOnEditorActionListener(new ConfirmOnEnterListener(() =>
        {
            confirm();
            return true;
        }));
    }

    private static bool TryPerformClick(View confirmView)
    {
        if (!confirmView.Enabled || confirmView.Visibility != ViewStates.Visible || !confirmView.IsShown)
            return false;

        confirmView.PerformClick();
        return true;
    }

    private sealed class ConfirmOnEnterListener : Java.Lang.Object, TextView.IOnEditorActionListener
    {
        private readonly Func<bool> _confirm;

        public ConfirmOnEnterListener(Func<bool> confirm)
            => _confirm = confirm;

        public bool OnEditorAction(TextView? v, ImeAction actionId, KeyEvent? e)
        {
            if (IsEnterKey(e))
            {
                return e?.Action == KeyEventActions.Up ? _confirm() : true;
            }

            return IsConfirmAction(actionId) && _confirm();
        }

        private static bool IsConfirmAction(ImeAction actionId)
        {
            int action = (int)actionId;
            return action == (int)ImeAction.Done
                   || action == (int)ImeAction.Go
                   || action == (int)ImeAction.Send;
        }

        private static bool IsEnterKey(KeyEvent? e)
            => e is not null
               && (e.KeyCode == Keycode.Enter || e.KeyCode == Keycode.NumpadEnter);
    }
}
