using System.Reflection;
using System.Windows.Forms;
using Deluno.Api.Network;
using Deluno.Tray;
using Xunit;

namespace Deluno.Tray.Tests;

/// <summary>
/// The picker opened by an ask (the Companion Tools page asking for NZBGet, or "Try again: install apps") has the apps ticked when
/// it opens. A ticked Usenet client needs its login typed, so the username and password boxes must be there from the first look:
/// before this was fixed they showed only after a box was ticked or unticked by hand, and Install selected refused with
/// "Choose a username and a password" and nowhere to type them (#1468).
/// </summary>
public sealed class ThePickerOpenedByAnAskShowsTheUsenetLoginTests
{
    private sealed class NothingInstalled : IInstalledProgramsReader
    {
        public IReadOnlyList<string> ReadDisplayNames() => [];
    }

    private static T Field<T>(object owner, string name) where T : class
        => (T)typeof(ComponentPickerDialog).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(owner)!;

    // WinForms controls are made on a single-threaded-apartment thread, as the tray makes them.
    private static void OnStaThread(Action body)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { body(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null)
        {
            throw new Exception("The dialog test failed on its thread.", failure);
        }
    }

    private static ComponentPickerDialog Open(params string[] preTick)
        => new(new ComponentInstallService(programs: new NothingInstalled()), preTick);

    [Theory]
    [InlineData("nzbget", "_nzbUser", "_nzbPassword")]
    [InlineData("sabnzbd", "_sabUser", "_sabPassword")]
    public void A_Usenet_client_ticked_by_the_ask_has_its_login_boxes_in_the_window_when_it_opens(string kind, string user, string password)
    {
        OnStaThread(() =>
        {
            using var dialog = Open(kind);
            var panel = Field<Panel>(dialog, "_usenetPanel");
            Assert.True(panel.Height > 0, "the login panel has room");
            Assert.True(dialog.ClientSize.Height > 500, "the window grew to hold the login boxes");
            var userBox = Field<TextBox>(dialog, user);
            var passwordBox = Field<TextBox>(dialog, password);
            Assert.True(userBox.Bottom <= panel.Height && passwordBox.Bottom <= panel.Height, "both boxes sit inside the panel");
            Assert.True(Field<Button>(dialog, "_btnInstall").Top > 462, "Install selected moved down below the boxes");
        });
    }

    [Fact]
    public void An_app_that_needs_no_login_ticked_by_the_ask_leaves_the_window_as_it_was()
    {
        OnStaThread(() =>
        {
            using var dialog = Open("qbittorrent");
            Assert.Equal(0, Field<Panel>(dialog, "_usenetPanel").Height);
            Assert.Equal(500, dialog.ClientSize.Height);
        });
    }
}
