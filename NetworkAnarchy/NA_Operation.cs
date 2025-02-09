﻿using ColossalFramework;
using ColossalFramework.UI;
using ICities;
using QCommonLib;
using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace NetworkAnarchy
{
    public partial class NetworkAnarchy : MonoBehaviour
    {
        public void Start()
        {
            Log = ModInfo.Log;

            Mods.NetworkSkins.Init();
            Mods.NetworkMultitool.Initialise();
            Mods.ZoningAdjuster.Initialise();

            // Getting NetTool
            m_netTool = GameObject.FindObjectsOfType<NetTool>().Where(x => x.GetType() == typeof(NetTool)).FirstOrDefault();
            if (m_netTool == null)
            {
                Log.Warning("NetTool not found.", "[NA26]");
                enabled = false;
                return;
            }

            // Getting BulldozeTool
            m_bulldozeTool = GameObject.FindObjectsOfType<BulldozeTool>().Where(x => x.GetType() == typeof(BulldozeTool)).FirstOrDefault();
            if (m_bulldozeTool == null)
            {
                Log.Warning("BulldozeTool not found.", "[NA27]");
                enabled = false;
                return;
            }

            // Getting BuildingTool
            m_buildingTool = GameObject.FindObjectsOfType<BuildingTool>().Where(x => x.GetType() == typeof(BuildingTool)).FirstOrDefault();
            if (m_buildingTool == null)
            {
                Log.Warning("BuildingTool not found.", "[NA28]");
                enabled = false;
                return;
            }

            // Getting NetTool private fields
            m_elevationField = m_netTool.GetType().GetField("m_elevation", BindingFlags.NonPublic | BindingFlags.Instance);
            m_elevationUpField = m_netTool.GetType().GetField("m_buildElevationUp", BindingFlags.NonPublic | BindingFlags.Instance);
            m_elevationDownField = m_netTool.GetType().GetField("m_buildElevationDown", BindingFlags.NonPublic | BindingFlags.Instance);
            m_buildingElevationField = m_buildingTool.GetType().GetField("m_elevation", BindingFlags.NonPublic | BindingFlags.Instance);
            m_controlPointCountField = m_netTool.GetType().GetField("m_controlPointCount", BindingFlags.NonPublic | BindingFlags.Instance);
            m_upgradingField = m_netTool.GetType().GetField("m_upgrading", BindingFlags.NonPublic | BindingFlags.Instance);
            m_placementErrorsField = m_buildingTool.GetType().GetField("m_placementErrors", BindingFlags.NonPublic | BindingFlags.Instance);

            if (m_elevationField == null || m_elevationUpField == null || m_elevationDownField == null || m_buildingElevationField == null || m_controlPointCountField == null || m_upgradingField == null || m_placementErrorsField == null)
            {
                Log.Warning("NetTool fields not found", "[NA25]");
                m_netTool = null;
                enabled = false;
                return;
            }

            bendingPrefabs.Clear();
            int count = PrefabCollection<NetInfo>.PrefabCount();
            for (uint i = 0; i < count; i++)
            {
                NetInfo prefab = PrefabCollection<NetInfo>.GetPrefab(i);
                if (prefab != null)
                {
                    if (prefab.m_enableBendingSegments)
                    {
                        bendingPrefabs.Add(prefab);
                    }
                }
            }

            ChirperManager.Initialise();

            // Getting Upgrade button template
            try
            {
                m_upgradeButtonTemplate = GameObject.Find("RoadsSmallPanel").GetComponent<GeneratedScrollPanel>().m_OptionsBar.Find<UIButton>("Upgrade");
            }
            catch
            {
                Log.Warning("Upgrade button template not found", "[NA24]");
            }

            // Creating UI
            CreateToolOptionsButton();

            // Store segment count
            m_segmentCount = NetManager.instance.m_segmentCount;

            // Getting control points
            try
            {
                m_controlPoints = m_netTool.GetType().GetField("m_controlPoints", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(m_netTool) as NetTool.ControlPoint[];
                m_cachedControlPoints = m_netTool.GetType().GetField("m_cachedControlPoints", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(m_netTool) as NetTool.ControlPoint[];
            }
            catch
            {
                Log.Warning("ControlPoints not found", "[NA21]");
            }

            // Init dictionary
            NetPrefab.Initialize();

            if (changeMaxTurnAngle.value)
            {
                NetPrefab.SetMaxTurnAngle(maxTurnAngle.value);
            }

            // Update Catenary
            UpdateCatenary();

            // Fix nodes
            FixNodes();

            // Load saved settings
            Anarchy = saved_anarchy.value;
            Bending = !saved_bending.value; // Toggle value to force prefab updates
            Bending = saved_bending.value;
            NodeSnapping = saved_nodeSnapping.value;
            Collision = saved_collision.value;
            StraightSlope = saved_smoothSlope.value;
            ChirperManager.UpdateAtlas();

            Log.Info("NetworkAnarchy Initialized", "[NA22]");
        }

        public void Update()
        {
            //UnityEngine.Debug.Log($"NAUpdate shouldShow:{(m_activated || !m_buttonInOptionsBar)} (activated:{m_activated}, inOptBar:{m_buttonInOptionsBar})\n" +
            //    $"prefab:{((m_netTool.enabled || m_bulldozeTool.enabled) ? m_netTool.m_prefab : null)}\n" +
            //    $"currentPref:{m_current} wasEnabled:{m_toolEnabled}, nowEnabled:{m_netTool.enabled}, buttonVis:{m_toolOptionButton.isVisible}, buttonChecked:{m_toolOptionButton.isChecked}");
            if (m_netTool == null)
            {
                return;
            }

            try
            {
                bool isRelevantToolActive = m_netTool.enabled || m_bulldozeTool.enabled || Mods.NetworkMultitool.IsToolActive() || Mods.ZoningAdjuster.IsToolActive() || IsBuildingIntersection();
                ToolBase toolBase = Singleton<ToolController>.instance.CurrentTool;

                // Getting selected prefab
                NetInfo prefab = isRelevantToolActive ? m_netTool.m_prefab : null;

                // Has the prefab or tool changed?
                if (prefab != m_current || toolBase != m_wasToolBase)
                {
                    Log.Debug($"Updating tool activation\n" +
                        $"  netTool:{m_netTool.enabled}, bulldoze:{m_bulldozeTool.enabled}, NMT:{Mods.NetworkMultitool.IsToolActive()}, ZA:{Mods.ZoningAdjuster.IsToolActive()}, Intersection:{IsBuildingIntersection()}\n" +
                        $"  prefab:{ModInfo.GetString(prefab)} (was:{ModInfo.GetString(m_current)}), RelevantTool:{isRelevantToolActive} Tool:{toolBase?.GetType()} (was:{m_wasToolBase?.GetType()})");

                    if (prefab == null)
                    {
                        Log.Debug($"Deactivating in Update because prefab is null");
                        Deactivate();
                    }
                    else
                    {
                        Activate(prefab);
                    }

                    m_wasToolBase = toolBase;

                    if (m_toolOptionButton != null)
                    {
                        //UnityEngine.Debug.Log($"WasVis:{m_toolOptionButton.isVisible}, NowVis:{m_activated || !m_buttonInOptionsBar} ({m_activated}, {m_buttonInOptionsBar})");
                        m_toolOptionButton.isVisible = IsActive;// || !m_buttonInOptionsBar;
                    }
                }

                // Plopping intersection?
                //if (m_buildingTool.enabled)
                //{
                //    if (!NetPrefab.SingleMode)
                //    {
                //        int elevation = (int)m_buildingElevationField.GetValue(m_buildingTool);
                //        NetPrefab.SingleMode = (elevation == 0);
                //    }
                //}
                //else
                //{
                //    NetPrefab.SingleMode = (QCommon.Scene == QCommon.SceneTypes.AssetEditor) && !UIView.HasModalInput() && !m_netTool.enabled && !m_bulldozeTool.enabled;
                //}
            }
            catch (Exception e)
            {
                Log.Error(e, "[NA46]");

                try
                {
                    Deactivate();
                }
                catch { }
            }
        }

        public void OnDisable()
        {
            if (Log != null)
                Log.Debug("Deactivating because OnDisable");
            else
                UnityEngine.Debug.Log($"Network Anarchy: Deactivating because OnDisable");
            Deactivate();
        }

        public class AfterSimulationTick : ThreadingExtensionBase
        {
            public override void OnAfterSimulationTick()
            {
                if (NetworkAnarchy.instance == null || !NetworkAnarchy.instance.enabled)
                {
                    return;
                }

                try
                {
                    NetworkAnarchy.instance.OnAfterSimulationTick();
                }
                catch (Exception e)
                {
                    Log.Error(e, "[NA23]");
                }
            }
        }

        public virtual void OnAfterSimulationTick()
        {
            if (m_buildingTool == null)
            {
                return;
            }

            // Removes HeightTooHigh & TooShort errors
            if (m_buildingTool.enabled)
            {
                var errors = (ToolBase.ToolErrors)m_placementErrorsField.GetValue(m_buildingTool);
                if ((errors & ToolBase.ToolErrors.HeightTooHigh) == ToolBase.ToolErrors.HeightTooHigh)
                {
                    errors &= ~ToolBase.ToolErrors.HeightTooHigh;
                    m_placementErrorsField.SetValue(m_buildingTool, errors);
                }

                if ((errors & ToolBase.ToolErrors.TooShort) == ToolBase.ToolErrors.TooShort)
                {
                    errors &= ~ToolBase.ToolErrors.TooShort;
                    m_placementErrorsField.SetValue(m_buildingTool, errors);
                }

                if ((errors & ToolBase.ToolErrors.SlopeTooSteep) == ToolBase.ToolErrors.SlopeTooSteep)
                {
                    errors &= ~ToolBase.ToolErrors.SlopeTooSteep;
                    m_placementErrorsField.SetValue(m_buildingTool, errors);
                }
            }

            // Resume fixes
            if (m_fixNodesCount != 0 || m_fixTunnelsCount != 0)
            {
                var prefab = NetPrefab.GetPrefab(m_current);
                if (prefab != null)
                {
                    prefab.Restore();
                }

                if (m_fixTunnelsCount != 0)
                {
                    FixTunnels();
                }

                if (m_fixNodesCount != 0)
                {
                    FixNodes();
                }

                if (prefab != null)
                {
                    prefab.Update();
                }
            }

            // Stop here if neither active nor bulldozer tool enabled
            if (!IsActive && !m_bulldozeTool.enabled)
            {
                return;
            }

            // Check if segment have been created/deleted/updated
            if (m_segmentCount != NetManager.instance.m_segmentCount || (bool)m_upgradingField.GetValue(m_netTool))
            {
                m_segmentCount = NetManager.instance.m_segmentCount;

                var prefab = NetPrefab.GetPrefab(m_current);
                if (prefab != null)
                {
                    prefab.Restore();
                }

                m_fixTunnelsCount = 0;
                m_fixNodesCount = 0;

                FixTunnels();
                FixNodes();

                if (prefab != null)
                {
                    prefab.Update();
                }
            }

            if (!IsActive)
            {
                return;
            }

            // Fix first control point elevation
            int count = (int)m_controlPointCountField.GetValue(m_netTool);
            if (count != m_controlPointCount && m_controlPointCount == 0 && count == 1)
            {
                if (FixControlPoint(0))
                {
                    m_elevation = Mathf.RoundToInt(Mathf.RoundToInt(m_controlPoints[0].m_elevation / elevationStep) * elevationStep * 256f / 12f);
                    UpdateElevation();
                    if (m_toolOptionButton != null)
                    {
                        m_toolOptionButton.UpdateButton();
                    }
                }
            }
            // Fix last control point elevation
            else if (count == ((m_netTool.m_mode == NetTool.Mode.Curved || m_netTool.m_mode == NetTool.Mode.Freeform) ? 2 : 1))
            {
                FixControlPoint(count);
            }
            m_controlPointCount = count;
        }

        private void Activate(NetInfo info)
        {
            Log.Debug($"Activated ({info})\n{new StackTrace()}", "[NA29]");

            if (info == null)
            {
                return;
            }

            var prefab = NetPrefab.GetPrefab(m_current);
            if (prefab != null)
            {
                prefab.Restore();
            }

            m_current = info;
            prefab = NetPrefab.GetPrefab(info);

            AttachToolOptionsButton(prefab);

            // Is it a valid prefab?
            //m_current.m_netAI.GetElevationLimits(out int min, out int max);

            //if ((m_bulldozeTool.enabled || (min == 0 && max == 0)) && !m_buttonExists)
            if (m_bulldozeTool.enabled && !DoesVanillaElevationButtonExist)
            {
                Log.Debug($"Deactivating because bulldozing non-network issue.\n" +
                    $"bulldoze:{m_bulldozeTool.enabled}, DVEBE:{DoesVanillaElevationButtonExist}");
                Deactivate();
                return;
            }

            DisableDefaultKeys();
            m_elevation = (int)m_elevationField.GetValue(m_netTool);
            if (prefab != null)
            {
                prefab.mode = mode;
                prefab.Update();
            }
            else
            {
                Log.Warning("Selected prefab not registered", "[NA30]");
            }

            m_segmentCount = NetManager.instance.m_segmentCount;
            m_controlPointCount = 0;

            IsActive = true;
            m_toolOptionButton.isVisible = true;
            m_toolOptionButton.UpdateButton();
        }

        private void Deactivate()
        {
            m_current = null;

            if (!IsActive)
            {
                return;
            }

            var prefab = NetPrefab.GetPrefab(m_current);
            if (prefab != null)
            {
                prefab.Restore();
            }

            RestoreDefaultKeys();

            IsActive = false;
            m_toolOptionButton.isVisible = false;

            Log.Debug($"Deactivated \n {new StackTrace().ToString()}");
        }

        internal bool IsBuildingIntersection()
        {
            if (m_buildingTool == null) return false;
            if (!m_buildingTool.enabled) return false;
            if (m_buildingTool.m_prefab == null) return false;
            if (!(m_buildingTool.m_prefab.GetAI() is IntersectionAI)) return false;
            return true;
        }

        private void DisableDefaultKeys()
        {
            if (m_keyDisabled)
            {
                return;
            }

            var emptyKey = new SavedInputKey("", Settings.gameSettingsFile);

            m_elevationUpField.SetValue(m_netTool, emptyKey);
            m_elevationDownField.SetValue(m_netTool, emptyKey);

            m_keyDisabled = true;
        }

        private void RestoreDefaultKeys()
        {
            if (!m_keyDisabled)
            {
                return;
            }

            m_elevationUpField.SetValue(m_netTool, OptionsKeymapping.elevationUp);
            m_elevationDownField.SetValue(m_netTool, OptionsKeymapping.elevationDown);

            m_keyDisabled = false;
        }

        private void UpdateElevation()
        {
            m_current.m_netAI.GetElevationLimits(out int min, out int max);

            m_elevation = Mathf.Clamp(m_elevation, min * 256, max * 256);
            if (elevationStep < 3)
            {
                m_elevation = Mathf.RoundToInt(Mathf.RoundToInt(m_elevation / (256f / 12f)) * (256f / 12f));
            }

            if ((int)m_elevationField.GetValue(m_netTool) != m_elevation)
            {
                m_elevationField.SetValue(m_netTool, m_elevation);
                m_toolOptionButton.UpdateButton();
            }
        }
    }
}
