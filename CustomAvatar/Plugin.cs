//  Beat Saber Custom Avatars - Custom player models for body presence in Beat Saber.
//  Copyright © 2018-2026  Nicolas Gnyra and Beat Saber Custom Avatars Contributors
//
//  This library is free software: you can redistribute it and/or
//  modify it under the terms of the GNU Lesser General Public
//  License as published by the Free Software Foundation, either
//  version 3 of the License, or (at your option) any later version.
//
//  This program is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
//  GNU Lesser General Public License for more details.
//
//  You should have received a copy of the GNU Lesser General Public License
//  along with this program.  If not, see <https://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using BGLib.AppFlow.Initialization;
using CustomAvatar.Logging;
using CustomAvatar.Player;
using CustomAvatar.Rendering;
using CustomAvatar.Rendering.Cameras;
using CustomAvatar.Zenject;
using CustomAvatar.Zenject.Internal;
using HarmonyLib;
using IPA;
using IPA.Loader;
using SiraUtil.Zenject;
using UnityEngine;
using Zenject;
using Logger = IPA.Logging.Logger;

namespace CustomAvatar
{
    [Plugin(RuntimeOptions.DynamicInit)]
    [HarmonyPatch]
    internal class Plugin
    {
        private static event Action<ProjectContext> _projectContextInstalling;

        private readonly Harmony _harmony = new("com.nicoco007.beatsabercustomavatars");

        private readonly Logger _ipaLogger;

        private readonly PluginMetadata _pluginMetadata;

        [Init]
        public Plugin(Logger ipaLogger, PluginMetadata pluginMetadata, Zenjector zenjector)
        {
            _ipaLogger = ipaLogger;
            _pluginMetadata = pluginMetadata;

            // can't inject at this point so just create it
            ILogger<Plugin> logger = new IPALogger<Plugin>(ipaLogger);

            logger.LogInformation("Initializing Custom Avatars");

            // 1.40 sira only has Mutate<T>(contract, callback). same attachments the old helper did
            ZenjectHelper.Init(ipaLogger);
            ZenjectHelper.AddComponentAlongsideExisting<MainCamera, MainCameraTracker>();
            ZenjectHelper.AddComponentAlongsideExisting<SmoothCamera, Rendering.Cameras.SmoothCamera>();
            ZenjectHelper.AddComponentAlongsideExisting<MenuEnvironmentManager, EnvironmentObject>();
            ZenjectHelper.AddComponentAlongsideExisting<MultiplayerLocalActivePlayerFacade, EnvironmentObject>("IsActiveObjects/Lasers");
            ZenjectHelper.AddComponentAlongsideExisting<MultiplayerLocalActivePlayerFacade, EnvironmentObject>("IsActiveObjects/Construction");
            ZenjectHelper.AddComponentAlongsideExisting<MultiplayerLocalActivePlayerFacade, EnvironmentObject>("IsActiveObjects/CenterRings");
            ZenjectHelper.AddComponentAlongsideExisting<MultiplayerLocalInactivePlayerFacade, EnvironmentObject>("MultiplayerLocalInactivePlayerPlayerPlace/CirclePlayerPlace");
            ZenjectHelper.AddComponentAlongsideExisting<MultiplayerConnectedPlayerFacade, EnvironmentObject>();
            ZenjectHelper.AddComponentAlongsideExisting<VRController, VRControllerVisuals>();

            zenjector.Install<MainMenuInstaller>(Location.Menu);
            // 1.40 sira has no Location.HealthWarning. the scene setup type is what 1.40.x uses
            zenjector.Install<HealthWarningInstaller, HealthWarningSceneSetup>();
            zenjector.Install<GameInstaller>(Location.Player);
        }

        [OnEnable]
        public void OnEnable()
        {
            _projectContextInstalling += ProjectContextInstalling;
            _harmony.PatchAll();
        }

        [OnDisable]
        public void OnDisable()
        {
            _harmony.UnpatchSelf();
            _projectContextInstalling -= ProjectContextInstalling;
        }

        private void ProjectContextInstalling(ProjectContext projectContext)
        {
            projectContext._normalInstallers.Add(projectContext.Container.Instantiate<ProjectInstaller>([_ipaLogger, _pluginMetadata]));
        }

        [HarmonyPatch(typeof(Context), nameof(Context.InstallInstallers), [typeof(List<InstallerBase>), typeof(List<Type>), typeof(List<ScriptableObjectInstaller>), typeof(List<MonoInstaller>), typeof(List<MonoInstaller>)])]
        [HarmonyPrefix]
        private static void Context_InstallInstallers(Context __instance)
        {
            if (__instance is ProjectContext projectContext)
            {
                _projectContextInstalling?.Invoke(projectContext);
            }
        }

        [HarmonyPatch(typeof(AsyncSceneContext), nameof(AsyncSceneContext.LoadInstallersAsync))]
        [HarmonyPrefix]
        private static void Prefix(AsyncSceneContext __instance)
        {
            if (__instance.name != "AppCoreSceneContext")
            {
                return;
            }

            __instance._asyncInstallers.Add(__instance.gameObject.AddComponent<CustomAvatarsInstaller>());
        }
    }
}
