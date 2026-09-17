using System.Windows.Controls.Primitives;

namespace NdzeVpn.Controls;

/// <summary>
/// A selectable button whose checked state comes only from its binding; clicking just runs Command.
///
/// Deliberately not a RadioButton. RadioButton's group logic writes IsChecked on its siblings when
/// one becomes checked, and with TwoWay bindings on views that load while collapsed it ends up
/// pushing the *last* option of a group into the view model — which silently changed the saved theme.
/// A plain ToggleButton that refuses to toggle itself has no such side channel.
/// </summary>
public sealed class SelectButton : ToggleButton
{
    protected override void OnToggle()
    {
    }
}
