using System;
using System.Collections;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using ScriptPortal.Vegas;
using VegasSurround71.UI;

namespace VegasSurround71
{
    /// <summary>
    /// Phase 4A application extension module. Registers the minimal dockable
    /// host plus explicit provisioning and validation commands.
    /// </summary>
    public sealed class ExtensionModule : ICustomCommandModule
    {
        public const string PanelInstanceName = "VegasSurround71HostPanel";
        public const string PanelCommandName = "VegasSurround71HostPanelCommand";
        public const string ProvisionCommandName = "VegasSurround71Provision71BussesCommand";
        public const string ValidationCommandName = "VegasSurround71RunHostValidationCommand";

        private Vegas _vegas;
        private CustomCommand _panelCommand;
        private CustomCommand _provisionCommand;
        private CustomCommand _validationCommand;
        private string _displayName;

        private string DisplayName
        {
            get
            {
                if (string.IsNullOrEmpty(_displayName))
                {
                    _displayName = ExtensionSettings.CurrentDisplayName;
                }

                return _displayName;
            }
        }

        public void InitializeModule(Vegas vegas)
        {
            _vegas = vegas;
            _displayName = ExtensionSettings.CurrentDisplayName;
        }

        public ICollection GetCustomCommands()
        {
            _panelCommand = new CustomCommand(CommandCategory.View, PanelCommandName);
            _panelCommand.DisplayName = DisplayName;
            _panelCommand.MenuSelectMessage =
                "Open " + DisplayName + " — 7.1 soundstage and master export";
            _panelCommand.Invoked += OnShowPanel;
            _panelCommand.MenuPopup += OnPanelMenuPopup;

            _provisionCommand = new CustomCommand(CommandCategory.Tools, ProvisionCommandName);
            _provisionCommand.DisplayName = DisplayName + " — Setup 7.1 Busses";
            _provisionCommand.MenuSelectMessage = "Create or reuse the eight [7.1] FL..SR audio busses";
            _provisionCommand.Invoked += OnProvision;

            _validationCommand = new CustomCommand(CommandCategory.Tools, ValidationCommandName);
            _validationCommand.DisplayName = DisplayName + " — Diagnostic";
            _validationCommand.MenuSelectMessage = "Run the in-host routing, undo, and diagnostic checks";
            _validationCommand.Invoked += OnValidate;

            return new CustomCommand[] { _panelCommand, _provisionCommand, _validationCommand };
        }

        private void OnShowPanel(object sender, EventArgs args)
        {
            try
            {
                if (!_vegas.ActivateDockView(PanelInstanceName))
                {
                    var panel = new HostPanel(PanelInstanceName, _vegas);
                    panel.DisplayName = DisplayName;
                    panel.PersistDockWindowState = true;
                    panel.DefaultFloatingSize = new Size(1120, 780);
                    panel.DefaultDockWindowStyle = DockWindowStyle.Floating;
                    panel.AutoLoadCommand = _panelCommand;
                    _vegas.LoadDockView(panel);
                }
            }
            catch (Exception ex)
            {
                ShowError("Unable to open the " + DisplayName + " host panel.", ex);
            }
        }

        private void OnPanelMenuPopup(object sender, EventArgs args)
        {
            try
            {
                _panelCommand.Checked = _vegas.FindDockView(PanelInstanceName);
            }
            catch (Exception)
            {
                _panelCommand.Checked = false;
            }
        }

        private void OnProvision(object sender, EventArgs args)
        {
            try
            {
                string error;
                if (!HostScriptBridge.RunProvision(_vegas, out error))
                {
                    throw new InvalidOperationException(error);
                }

                int present = 0;
                if (_vegas.Project != null)
                {
                    foreach (BusTrack bus in _vegas.Project.BusTracks)
                    {
                        var audio = bus as AudioBusTrack;
                        if (audio == null || !audio.IsAudio() || audio.IsMaster())
                        {
                            continue;
                        }

                        if (IsCanonicalBus(audio))
                        {
                            present++;
                        }
                    }
                }

                MessageBox.Show(
                    string.Format("{0}/8 canonical 7.1 busses are available.\r\n\r\n" +
                                  "Mono render mapping: FL=0, FR=1, FC=2, LFE=3, BL=4, BR=5, SL=6, SR=7.",
                        present),
                    "TellTale 7-Point-One",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                ShowError("Unable to provision the 7.1 bus topology.", ex);
            }
        }

        private void OnValidate(object sender, EventArgs args)
        {
            try
            {
                string reportPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "TellTale7PointOne", "TellTale 7-Point-One Diagnostic.txt");
                ValidationOutcome outcome = HostScriptBridge.RunValidation(
                    _vegas, reportPath, false);
                MessageBox.Show(
                    string.Format("{0} checks passed, {1} failed.\r\n\r\nReport: {2}",
                        outcome.PassedCount, outcome.FailedCount, reportPath),
                    "TellTale 7-Point-One Diagnostic",
                    MessageBoxButtons.OK,
                    outcome.Passed ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            }
            catch (Exception ex)
            {
                ShowError("Unable to run the in-host validation.", ex);
            }
        }

        private static bool IsCanonicalBus(AudioBusTrack bus)
        {
            for (int i = 0; i < Bus71Layout.BusNames.Length; i++)
            {
                if (string.Equals(bus.Description, Bus71Layout.BusNames[i],
                    StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        private void ShowError(string message, Exception ex)
        {
            MessageBox.Show(
                message + "\r\n\r\n" + ex.Message,
                DisplayName,
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }
}
