using System;
using HMUI;
using Zenject;

namespace MultiplayerChat.UI;

public class CustomAvatarsSettingsFlowCoordinator : FlowCoordinator
{
    [Inject] private readonly CustomAvatarsSettingsViewController _customAvatarsView = null!;

    public HMUI.FlowCoordinator? ParentFlow { get; set; }

    protected override void DidActivate(bool firstActivation, bool addedToHierarchy, bool screenSystemEnabling)
    {
        if (firstActivation)
        {
            showBackButton = true;
            SetTitle("Custom Multiplayer Avatars");
            ProvideInitialViewControllers(_customAvatarsView);
        }

        if (addedToHierarchy)
            _customAvatarsView.CustomAvatarsSettingsApplied += OnApplied;
    }

    protected override void DidDeactivate(bool removedFromHierarchy, bool screenSystemDisabling)
    {
        if (removedFromHierarchy)
            _customAvatarsView.CustomAvatarsSettingsApplied -= OnApplied;
    }

    private void OnApplied() => Dismiss();

    protected override void BackButtonWasPressed(ViewController topViewController) => Dismiss();

    private void Dismiss()
    {
        if (ParentFlow != null)
            ParentFlow.DismissFlowCoordinator(this);
        else
            BeatSaberMarkupLanguage.BeatSaberUI.MainFlowCoordinator.DismissFlowCoordinator(this);
    }
}
