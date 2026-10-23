using FalconBMS.Launcher.Input;
using FalconBMS.Launcher.Models;
using FalconBMS.Launcher.Utils;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using Vortice.DirectInput;
using DiKey = Vortice.DirectInput.Key;

namespace FalconBMS.Launcher.ViewModels;

public sealed class KeyMappingWindowViewModel : ViewModelBase, IDisposable
{
    private const string PovInvokeDefault = "Default";
    private const string PovInvokeShift = "Shift";

    private readonly BindingRow _row;
    private readonly List<BindingRow> _profileRows;
    private readonly List<DeviceBindingProfile> _deviceProfiles;
    private readonly string _aircraftProfileName;
    private readonly Action<BindingRow, string, int, string, int> _saveKeyboardBinding;
    private readonly Action<BindingRow, string?, int?, int?> _saveDeviceButtonBinding;
    private readonly Action<BindingRow, string?, int?, int?, string?> _saveDevicePovBinding;
    private readonly Action _closeWindow;
    private readonly DirectInputCaptureHost _captureHost = new();

    private string _tempKeyScancode;
    private int _tempModifierFlags;
    private string _tempChordScancode;
    private int _tempChordModifierFlags;
    private bool _isReservedKeyWarningActive;

    private readonly List<PendingDxButton> _pendingDxButtons = new();
    private readonly List<PendingDxPov> _pendingDxPovs = new();

    private sealed class PendingDxButton
    {
        public string DeviceKey { get; set; } = "";
        public int ButtonIndex { get; set; }
        public int AssignmentIndex { get; set; }
    }

    private sealed class PendingDxPov
    {
        public string DeviceKey { get; set; } = "";
        public int PovIndex { get; set; }
        public int Direction { get; set; }
        public string Invoke { get; set; } = PovInvokeDefault;
    }

    public string TitleText { get; }

    private string _keyboardAssignmentText;
    public string KeyboardAssignmentText
    {
        get => _keyboardAssignmentText;
        private set => Set(ref _keyboardAssignmentText, value);
    }

    private string _keyboardConflictText = "";
    public string KeyboardConflictText
    {
        get => _keyboardConflictText;
        private set => Set(ref _keyboardConflictText, value);
    }

    private string _dxConflictText = "";
    public string DxConflictText
    {
        get => _dxConflictText;
        private set => Set(ref _dxConflictText, value);
    }

    // All assigned DX inputs, followed by one permanent awaiting-input row
    public ObservableCollection<DxInputRowViewModel> DxInputRows { get; } = new();

    private readonly DxInputRowViewModel _awaitingDxRow;

    // Preserve the user's manual selection while physical DX Shift is held
    private string _awaitingDxPresetShiftState =
        DeviceButtonBinding.ShiftStateUnshifted;

    // Prevent automatic changes from overwriting the manual selection
    private bool _updatingAwaitingDxShiftState;

    public ICommand ClearKeyCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand CancelCommand { get; }

    public KeyMappingWindowViewModel(
        BindingRow row,
        IEnumerable<BindingRow> profileRows,
        IEnumerable<DeviceBindingProfile> deviceProfiles,
        string aircraftProfileName,
        Action<BindingRow, string, int, string, int> saveKeyboardBinding,
        Action<BindingRow, string?, int?, int?> saveDeviceButtonBinding,
        Action<BindingRow, string?, int?, int?, string?> saveDevicePovBinding,
        Action closeWindow)
    {
        _row = row;
        _profileRows = profileRows.ToList();
        _deviceProfiles = deviceProfiles.ToList();
        _aircraftProfileName = aircraftProfileName;
        _saveKeyboardBinding = saveKeyboardBinding;
        _saveDeviceButtonBinding = saveDeviceButtonBinding;
        _saveDevicePovBinding = saveDevicePovBinding;
        _closeWindow = closeWindow;

        _captureHost.KeyboardInput +=
            CaptureSession_KeyboardInput;

        _captureHost.JoystickButtonInput +=
            CaptureSession_JoystickButtonInput;

        _captureHost.JoystickPovInput +=
            CaptureSession_JoystickPovInput;

        TitleText = row.Description;

        _tempKeyScancode = row.KeyScancode;
        _tempModifierFlags = row.KeyModifierFlags;
        _tempChordScancode = row.ChordScancode;
        _tempChordModifierFlags = row.ChordModifierFlags;

        // The waiting input row is always the final row in the table.
        // Its selections determine how the next captured input is assigned.
        _awaitingDxRow = new DxInputRowViewModel
        {
            IsAwaitingInput = true,
            // Shown in the "Assigned Button" column until a button is pressed
            InputText = "Press any button to assign",
            IsShiftSelectionEnabled =
                !DeviceButtonBinding.IsDxShiftCallback(_row.CallbackName)
        };

        // Remember manual Shift selections independently of physical Shift
        _awaitingDxRow.PropertyChanged += AwaitingDxRow_PropertyChanged;

        DxInputRows.Add(_awaitingDxRow);

        // Load saved assignments as individually editable rows
        LoadExistingDxBindings(row.CallbackName);

        _keyboardAssignmentText = BuildKeyboardAssignmentPreview();
        UpdateConflict();

        ClearKeyCommand = new RelayCommand(() =>
        {
            _tempKeyScancode = "0xFFFFFFFF";
            _tempModifierFlags = 0;
            _tempChordScancode = "0";
            _tempChordModifierFlags = 0;

            UpdateKeyboardAssignmentPreview();
            UpdateConflict();
        });

        SaveCommand = new RelayCommand(() =>
        {
            _saveKeyboardBinding(
                _row,
                _tempKeyScancode,
                _tempModifierFlags,
                _tempChordScancode,
                _tempChordModifierFlags);

            // Replace this callback's DX list with the current pending list.
            // Treats DX as button OR POV, so this clear removes both.
            _saveDeviceButtonBinding(_row, null, null, null);

            foreach (PendingDxButton pendingDxButton in _pendingDxButtons)
            {
                _saveDeviceButtonBinding(
                    _row,
                    pendingDxButton.DeviceKey,
                    pendingDxButton.ButtonIndex,
                    pendingDxButton.AssignmentIndex);
            }

            foreach (PendingDxPov pendingDxPov in _pendingDxPovs)
            {
                _saveDevicePovBinding(
                    _row,
                    pendingDxPov.DeviceKey,
                    pendingDxPov.PovIndex,
                    pendingDxPov.Direction,
                    pendingDxPov.Invoke);
            }

            _closeWindow();
        });

        CancelCommand = new RelayCommand(_closeWindow);
    }

    public void StartCapture()
    {
        StopCapture();

        IEnumerable<DirectInputCaptureDevice> joystickDevices =
            _deviceProfiles
                .Where(device =>
                    device.IsConnected &&
                    (device.ButtonCount > 0 ||
                     device.PovCount > 0))
                .Select(device =>
                    new DirectInputCaptureDevice(
                        device.DurableDeviceKey,
                        device.InstanceGuid));

        _captureHost.Start(
            Application.Current.Dispatcher,
            captureKeyboard: true,
            joystickDevices: joystickDevices);

        // Reflect a physical DX Shift button that may already be held
        UpdateAwaitingDxShiftDisplay();
    }

    public void StopCapture()
    {
        _captureHost.Stop();
    }

    private void CaptureSession_KeyboardInput(
        object? sender,
        BufferedKeyboardInputEventArgs e)
    {
        // Ignore capture input while the reserved-key warning is showing, 
        // so repeated key events don't display more warning windows
        if (_isReservedKeyWarningActive)
            return;

        // Existing Key Mapping behavior creates an assignment on the
        // non-modifier key DOWN event. Modifier-only presses do not map.
        if (!e.IsPressed)
            return;

        DiKey caught =
            e.Key;

        if (caught == DiKey.Unknown ||
            caught == DiKey.LeftShift ||
            caught == DiKey.RightShift ||
            caught == DiKey.LeftControl ||
            caught == DiKey.RightControl ||
            caught == DiKey.LeftAlt ||
            caught == DiKey.RightAlt)
        {
            return;
        }

        int modifierFlags =
            e.ModifierFlags;

        // Reserve keys for BMS and Windows
        if (ReservedKeyboardBindings.TryGetDisplayText(
                caught,
                modifierFlags,
                out string reservedBinding))
        {
            _isReservedKeyWarningActive = true;

            try
            {
                MessageBox.Show(
                    reservedBinding +
                    " is reserved and cannot be reassigned.",
                    "Reserved Key",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            finally
            {
                _isReservedKeyWarningActive = false;
            }

            return;
        }

        _tempKeyScancode =
            "0x" +
            ((int)caught).ToString("X");

        _tempModifierFlags =
            modifierFlags;

        _tempChordScancode =
            "0";

        _tempChordModifierFlags =
            0;

        UpdateKeyboardAssignmentPreview();
        UpdateConflict();
    }


    private void CaptureSession_JoystickButtonInput(
        object? sender,
        BufferedJoystickButtonEventArgs e)
    {
        // A previously assigned DX Shift button acts as a modifier.
        // Update the awaiting row on both DOWN and UP events.
        //
        // When editing the DX Shift callback itself, the button
        // must remain available for assignment.
        if (!DeviceButtonBinding.IsDxShiftCallback(_row.CallbackName) &&
            IsAssignedDxShiftButton(e.DeviceKey, e.ButtonIndex))
        {
            UpdateAwaitingDxShiftDisplay();
            return;
        }

        // Normal DX assignments are created only on button down/press
        if (!e.IsPressed)
            return;

        // Ensure the displayed state reflects any held DX Shift
        UpdateAwaitingDxShiftDisplay();

        int previousCount = _pendingDxButtons.Count;

        AddPendingDxButton(
            e.DeviceKey,
            e.ButtonIndex);

        // Reset the manual preset for the next assignment.
        // If physical Shift remains held, the dropdown stays Shifted.
        if (_pendingDxButtons.Count > previousCount)
            ResetAwaitingDxOptions();

        UpdateKeyboardAssignmentPreview();
        UpdateConflict();
    }

    private void CaptureSession_JoystickPovInput(
        object? sender,
        BufferedJoystickPovEventArgs e)
    {
        int? direction = e.Direction;

        // Ignore POV returning to center
        if (!direction.HasValue)
            return;

        int previousCount = _pendingDxPovs.Count;

        AddPendingDxPov(
            e.DeviceKey,
            e.PovIndex,
            direction.Value);

        if (_pendingDxPovs.Count > previousCount)
            ResetAwaitingDxOptions();

        UpdateKeyboardAssignmentPreview();
        UpdateConflict();
    }

    private void UpdateConflict()
    {
        string selectedAssignment = BuildKeyboardAssignmentText();

        string keyboardConflict = "";

        if (!string.IsNullOrWhiteSpace(selectedAssignment))
        {
            BindingRow? conflict = _profileRows.FirstOrDefault(row =>
                !ReferenceEquals(row, _row) &&
                row.IsEditable &&
                string.Equals(GetAssignmentText(row), selectedAssignment, StringComparison.OrdinalIgnoreCase));

            if (conflict is not null)
                keyboardConflict = "\'" + selectedAssignment + "\' is currently being used by: " + conflict.Description.Trim() + "";
        }

        KeyboardConflictText = string.IsNullOrWhiteSpace(keyboardConflict)
            ? ""
            : keyboardConflict + "\nClick \"Save\" to replace this anyway.";

        var dxConflicts = new List<string>();

        foreach (PendingDxButton pendingDxButton in _pendingDxButtons)
        {
            DeviceBindingProfile? device = _deviceProfiles.FirstOrDefault(d =>
                string.Equals(d.DurableDeviceKey, pendingDxButton.DeviceKey, StringComparison.OrdinalIgnoreCase));

            DeviceAircraftBindingProfile? aircraft = device?.AircraftProfiles.FirstOrDefault(profile =>
                string.Equals(profile.AircraftProfile, _aircraftProfileName, StringComparison.OrdinalIgnoreCase));

            DeviceButtonBinding? conflict = aircraft?.ButtonBindings.FirstOrDefault(binding =>
                binding.ButtonIndex == pendingDxButton.ButtonIndex &&
                binding.AssignmentIndex == pendingDxButton.AssignmentIndex &&
                !string.Equals(binding.CallbackName, _row.CallbackName, StringComparison.OrdinalIgnoreCase));

            if (conflict is null)
                continue;

            BindingRow? conflictRow = _profileRows.FirstOrDefault(row =>
                string.Equals(row.CallbackName, conflict.CallbackName, StringComparison.OrdinalIgnoreCase));

            string deviceName = device?.ProductName
                ?? device?.InstanceName
                ?? pendingDxButton.DeviceKey;

            dxConflicts.Add(
                deviceName +
                " " +
                DeviceButtonBinding.BuildDisplayText(
                    pendingDxButton.ButtonIndex,
                    pendingDxButton.AssignmentIndex) +
                " is currently being used by: " +
                (conflictRow?.Description.Trim() ?? conflict.CallbackName));
        }

        foreach (PendingDxPov pendingDxPov in _pendingDxPovs)
        {
            DeviceBindingProfile? device = _deviceProfiles.FirstOrDefault(d =>
                string.Equals(d.DurableDeviceKey, pendingDxPov.DeviceKey, StringComparison.OrdinalIgnoreCase));

            DeviceAircraftBindingProfile? aircraft = device?.AircraftProfiles.FirstOrDefault(profile =>
                string.Equals(profile.AircraftProfile, _aircraftProfileName, StringComparison.OrdinalIgnoreCase));

            DevicePovBinding? conflict = aircraft?.PovBindings.FirstOrDefault(binding =>
                binding.PovIndex == pendingDxPov.PovIndex &&
                binding.Direction == pendingDxPov.Direction &&
                string.Equals(NormalizePovInvoke(binding.Invoke), pendingDxPov.Invoke, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(binding.CallbackName, _row.CallbackName, StringComparison.OrdinalIgnoreCase));

            if (conflict is null)
                continue;

            BindingRow? conflictRow = _profileRows.FirstOrDefault(row =>
                string.Equals(row.CallbackName, conflict.CallbackName, StringComparison.OrdinalIgnoreCase));

            string deviceName = device?.ProductName
                ?? device?.InstanceName
                ?? pendingDxPov.DeviceKey;

            dxConflicts.Add(
                deviceName +
                " " +
                BuildPovDisplayText(
                    pendingDxPov.PovIndex,
                    pendingDxPov.Direction,
                    pendingDxPov.Invoke) +
                " is currently being used by: " +
                (conflictRow?.Description.Trim() ?? conflict.CallbackName));
        }

        List<string> distinctDxConflicts = dxConflicts
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        DxConflictText = distinctDxConflicts.Count == 0
            ? ""
            : string.Join("\n", distinctDxConflicts) + "\nClick \"Save\" to replace this anyway.";
    }

    private static string GetAssignmentText(BindingRow row)
    {
        return KeyAssgn.GetKeyAssignmentStatus(
            row.KeyScancode,
            row.KeyModifierFlags,
            row.ChordScancode,
            row.ChordModifierFlags);
    }

    private void UpdateKeyboardAssignmentPreview()
    {
        KeyboardAssignmentText = BuildKeyboardAssignmentPreview();
    }

    private string BuildKeyboardAssignmentPreview()
    {
        string keyText = BuildKeyboardAssignmentText();

        return string.IsNullOrWhiteSpace(keyText)
            ? "Press any key to assign"
            : keyText;
    }

    private string BuildKeyboardAssignmentText()
    {
        return KeyAssgn.GetKeyAssignmentStatus(
            _tempKeyScancode,
            _tempModifierFlags,
            _tempChordScancode,
            _tempChordModifierFlags);
    }

    private void LoadExistingDxBindings(string callbackName)
    {
        foreach (DeviceBindingProfile device in _deviceProfiles)
        {
            DeviceAircraftBindingProfile? aircraft =
                GetActiveAircraftProfile(device);

            if (aircraft is null)
                continue;

            // Load each existing DX button assignment.
            // Its saved assignment index determines the row's
            // initial Shift State and Trigger selections.
            foreach (DeviceButtonBinding binding in aircraft.ButtonBindings
                         .Where(button =>
                             string.Equals(
                                 button.CallbackName,
                                 callbackName,
                                 StringComparison.OrdinalIgnoreCase))
                         .OrderBy(button => button.ButtonIndex)
                         .ThenBy(button => button.AssignmentIndex))
            {
                int assignmentIndex =
                    DeviceButtonBinding.NormalizeAssignmentIndexForCallback(
                        callbackName,
                        binding.AssignmentIndex);

                AddPendingDxButton(
                    device.DurableDeviceKey,
                    binding.ButtonIndex,
                    assignmentIndex);
            }

            // POV directions also appear as seperate DX rows
            foreach (DevicePovBinding binding in aircraft.PovBindings
                         .Where(pov =>
                             string.Equals(
                                 pov.CallbackName,
                                 callbackName,
                                 StringComparison.OrdinalIgnoreCase))
                         .OrderBy(pov => pov.PovIndex)
                         .ThenBy(pov => pov.Direction)
                         .ThenBy(pov => pov.Invoke))
            {
                AddPendingDxPov(
                    device.DurableDeviceKey,
                    binding.PovIndex,
                    binding.Direction,
                    NormalizePovInvoke(binding.Invoke));
            }
        }

        // The awaiting-input row keeps its own defaults:
        // Unshifted + Press
    }

    private void AddPendingDxButton(string deviceKey, int buttonIndex)
    {
        if (DeviceButtonBinding.IsDxShiftCallback(_row.CallbackName))
        {
            // DX Shift callbacks must occupy both
            AddPendingDxButton(deviceKey, buttonIndex, 0);
            AddPendingDxButton(deviceKey, buttonIndex, 1);
            return;
        }

        // The awaiting row supplies the default shift state
        string shiftState = _awaitingDxRow.ShiftState;

        // Holding an assigned physical DX Shift button always
        // overrides an Unshifted preset.
        if (IsPhysicalDxShiftHeld())
            shiftState = DeviceButtonBinding.ShiftStateShifted;

        string trigger = _awaitingDxRow.Trigger;

        int assignmentIndex =
            DeviceButtonBinding.GetAssignmentIndex(
                shiftState,
                trigger);

        AddPendingDxButton(
            deviceKey,
            buttonIndex,
            assignmentIndex);
    }

    private void AddPendingDxButton(
        string deviceKey,
        int buttonIndex,
        int assignmentIndex)
    {
        assignmentIndex =
            DeviceButtonBinding.NormalizeAssignmentIndexForCallback(
                _row.CallbackName,
                assignmentIndex);

        bool alreadyPending = _pendingDxButtons.Any(button =>
            string.Equals(
                button.DeviceKey,
                deviceKey,
                StringComparison.OrdinalIgnoreCase) &&
            button.ButtonIndex == buttonIndex &&
            button.AssignmentIndex == assignmentIndex);

        if (alreadyPending)
            return;

        _pendingDxButtons.Add(new PendingDxButton
        {
            DeviceKey = deviceKey,
            ButtonIndex = buttonIndex,
            AssignmentIndex = assignmentIndex
        });

        bool isDxShiftCallback =
            DeviceButtonBinding.IsDxShiftCallback(_row.CallbackName);

        // The same physical DX Shift button uses two internal slots,
        // but should appear as only one row in the UI
        bool rowAlreadyExists = DxInputRows.Any(input =>
            !input.IsAwaitingInput &&
            input.ButtonIndex == buttonIndex &&
            string.Equals(
                input.DeviceKey,
                deviceKey,
                StringComparison.OrdinalIgnoreCase) &&
            (isDxShiftCallback ||
             DeviceButtonBinding.GetAssignmentIndex(
                 input.ShiftState,
                 input.Trigger) == assignmentIndex));

        if (rowAlreadyExists)
            return;

        AddDxInputRow(
            deviceKey,
            buttonIndex,
            null,
            null,
            isDxShiftCallback
                ? DeviceButtonBinding.ShiftStateUnshifted
                : DeviceButtonBinding.GetShiftState(assignmentIndex),
            isDxShiftCallback
                ? DeviceButtonBinding.TriggerPress
                : DeviceButtonBinding.GetTrigger(assignmentIndex));
    }

    private void AddPendingDxPov(
        string deviceKey,
        int povIndex,
        int direction)
    {
        bool isShifted =
            !DeviceButtonBinding.IsDxShiftCallback(_row.CallbackName) &&
            (_awaitingDxRow.ShiftState ==
                 DeviceButtonBinding.ShiftStateShifted ||
             IsPhysicalDxShiftHeld());

        string invoke =
            isShifted ? PovInvokeShift : PovInvokeDefault;

        AddPendingDxPov(
            deviceKey,
            povIndex,
            direction,
            invoke);
    }

    private void AddPendingDxPov(
        string deviceKey,
        int povIndex,
        int direction,
        string invoke)
    {
        invoke = NormalizePovInvoke(invoke);

        bool alreadyPending = _pendingDxPovs.Any(pov =>
            string.Equals(
                pov.DeviceKey,
                deviceKey,
                StringComparison.OrdinalIgnoreCase) &&
            pov.PovIndex == povIndex &&
            pov.Direction == direction &&
            string.Equals(
                pov.Invoke,
                invoke,
                StringComparison.OrdinalIgnoreCase));

        if (alreadyPending)
            return;

        _pendingDxPovs.Add(new PendingDxPov
        {
            DeviceKey = deviceKey,
            PovIndex = povIndex,
            Direction = direction,
            Invoke = invoke
        });

        string shiftState =
            invoke == PovInvokeShift
                ? DeviceButtonBinding.ShiftStateShifted
                : DeviceButtonBinding.ShiftStateUnshifted;

        AddDxInputRow(
            deviceKey,
            null,
            povIndex,
            direction,
            shiftState,
            DeviceButtonBinding.TriggerPress);
    }

    private void AddDxInputRow(
        string deviceKey,
        int? buttonIndex,
        int? povIndex,
        int? direction,
        string shiftState,
        string trigger)
    {
        string inputName;

        if (buttonIndex.HasValue)
        {
            inputName = "DX" + (buttonIndex.Value + 1);
        }
        else
        {
            inputName = BuildPovDisplayText(
                povIndex.GetValueOrDefault(),
                direction.GetValueOrDefault(),
                PovInvokeDefault);
        }

        var inputRow = new DxInputRowViewModel
        {
            DeviceKey = deviceKey,
            ButtonIndex = buttonIndex,
            PovIndex = povIndex,
            Direction = direction,
            InputText = GetDeviceDisplayName(deviceKey) + " " + inputName,
            ShiftState = shiftState,
            Trigger = trigger,
            IsShiftSelectionEnabled =
                !DeviceButtonBinding.IsDxShiftCallback(_row.CallbackName)
        };

        // Each assigned row removes only itself
        inputRow.ClearCommand =
            new RelayCommand(() => RemoveDxInputRow(inputRow));

        // Detect changes to an existing row's dropdown selections
        inputRow.PropertyChanged += DxInputRow_PropertyChanged;

        // Always insert immediately above the awaiting input row
        DxInputRows.Insert(DxInputRows.Count - 1, inputRow);
    }

    private void RemoveDxInputRow(DxInputRowViewModel inputRow)
    {
        if (inputRow.IsAwaitingInput)
            return;

        inputRow.PropertyChanged -= DxInputRow_PropertyChanged;

        if (!DxInputRows.Remove(inputRow))
            return;

        RebuildPendingDxBindingsFromRows();
        UpdateKeyboardAssignmentPreview();
        UpdateConflict();
    }

    private void DxInputRow_PropertyChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        if (sender is not DxInputRowViewModel changedRow)
            return;

        if (e.PropertyName != nameof(DxInputRowViewModel.ShiftState) &&
            e.PropertyName != nameof(DxInputRowViewModel.Trigger))
        {
            return;
        }

        // If editing a row creates a duplicate physical input and slot,
        // retain the edited row and remove the redundant assignment
        foreach (DxInputRowViewModel duplicate in DxInputRows
                     .Where(input =>
                         !input.IsAwaitingInput &&
                         !ReferenceEquals(input, changedRow) &&
                         string.Equals(
                             input.DeviceKey,
                             changedRow.DeviceKey,
                             StringComparison.OrdinalIgnoreCase) &&
                         input.ButtonIndex == changedRow.ButtonIndex &&
                         input.PovIndex == changedRow.PovIndex &&
                         input.Direction == changedRow.Direction &&
                         input.ShiftState == changedRow.ShiftState &&
                         (input.IsPov ||
                          input.Trigger == changedRow.Trigger))
                     .ToList())
        {
            duplicate.PropertyChanged -= DxInputRow_PropertyChanged;
            DxInputRows.Remove(duplicate);
        }

        RebuildPendingDxBindingsFromRows();
        UpdateKeyboardAssignmentPreview();
        UpdateConflict();
    }

    private void RebuildPendingDxBindingsFromRows()
    {
        _pendingDxButtons.Clear();
        _pendingDxPovs.Clear();

        bool isDxShiftCallback =
            DeviceButtonBinding.IsDxShiftCallback(_row.CallbackName);

        foreach (DxInputRowViewModel input in DxInputRows)
        {
            if (input.IsAwaitingInput)
                continue;

            if (input.ButtonIndex.HasValue)
            {
                // A DX Shift callback always uses both press slots
                int[] assignmentIndices = isDxShiftCallback
                    ? new[] { 0, 1 }
                    : new[]
                    {
                        DeviceButtonBinding.GetAssignmentIndex(
                            input.ShiftState,
                            input.Trigger)
                    };

                foreach (int assignmentIndex in assignmentIndices)
                {
                    bool exists = _pendingDxButtons.Any(button =>
                        string.Equals(
                            button.DeviceKey,
                            input.DeviceKey,
                            StringComparison.OrdinalIgnoreCase) &&
                        button.ButtonIndex == input.ButtonIndex.Value &&
                        button.AssignmentIndex == assignmentIndex);

                    if (exists)
                        continue;

                    _pendingDxButtons.Add(new PendingDxButton
                    {
                        DeviceKey = input.DeviceKey,
                        ButtonIndex = input.ButtonIndex.Value,
                        AssignmentIndex = assignmentIndex
                    });
                }
            }
            else if (input.PovIndex.HasValue &&
                     input.Direction.HasValue)
            {
                string invoke =
                    input.ShiftState == DeviceButtonBinding.ShiftStateShifted
                        ? PovInvokeShift
                        : PovInvokeDefault;

                bool exists = _pendingDxPovs.Any(pov =>
                    string.Equals(
                        pov.DeviceKey,
                        input.DeviceKey,
                        StringComparison.OrdinalIgnoreCase) &&
                    pov.PovIndex == input.PovIndex.Value &&
                    pov.Direction == input.Direction.Value &&
                    string.Equals(
                        pov.Invoke,
                        invoke,
                        StringComparison.OrdinalIgnoreCase));

                if (exists)
                    continue;

                _pendingDxPovs.Add(new PendingDxPov
                {
                    DeviceKey = input.DeviceKey,
                    PovIndex = input.PovIndex.Value,
                    Direction = input.Direction.Value,
                    Invoke = invoke
                });
            }
        }
    }

    private void ResetAwaitingDxOptions()
    {
        // Reset the user's preset for the next DX assignment
        _awaitingDxPresetShiftState =
            DeviceButtonBinding.ShiftStateUnshifted;

        _awaitingDxRow.Trigger =
            DeviceButtonBinding.TriggerPress;

        // Physical Shift still takes priority while held
        UpdateAwaitingDxShiftDisplay();
    }

    private void AwaitingDxRow_PropertyChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(DxInputRowViewModel.ShiftState))
            return;

        // An automatic display update is not a manual selection
        if (_updatingAwaitingDxShiftState)
            return;

        // Remember the user's selection so it can be restored
        // when the physical DX Shift button is released
        _awaitingDxPresetShiftState = _awaitingDxRow.ShiftState;

        // If physical Shift is held, it continues to take priority
        UpdateAwaitingDxShiftDisplay();
    }

    private void UpdateAwaitingDxShiftDisplay()
    {
        // DX Shift callbacks have fixed assignment states
        if (!_awaitingDxRow.IsShiftSelectionEnabled)
            return;

        string displayedShiftState = IsPhysicalDxShiftHeld()
            ? DeviceButtonBinding.ShiftStateShifted
            : _awaitingDxPresetShiftState;

        if (_awaitingDxRow.ShiftState == displayedShiftState)
            return;

        // Update the dropdown without changing the manual preset
        _updatingAwaitingDxShiftState = true;

        try
        {
            _awaitingDxRow.ShiftState = displayedShiftState;
        }
        finally
        {
            _updatingAwaitingDxShiftState = false;
        }
    }

    private DeviceAircraftBindingProfile? GetActiveAircraftProfile(
        DeviceBindingProfile device)
    {
        return device.AircraftProfiles.FirstOrDefault(profile =>
            string.Equals(
                profile.AircraftProfile,
                _aircraftProfileName,
                StringComparison.OrdinalIgnoreCase));
    }

    private string GetDeviceDisplayName(string deviceKey)
    {
        DeviceBindingProfile? device = _deviceProfiles.FirstOrDefault(
            profile => string.Equals(
                profile.DurableDeviceKey,
                deviceKey,
                StringComparison.OrdinalIgnoreCase));

        return device?.ProductName
            ?? device?.InstanceName
            ?? deviceKey;
    }

    private bool IsAssignedDxShiftButton(
        string deviceKey,
        int buttonIndex)
    {
        DeviceBindingProfile? device = _deviceProfiles.FirstOrDefault(
            profile => string.Equals(
                profile.DurableDeviceKey,
                deviceKey,
                StringComparison.OrdinalIgnoreCase));

        if (device is null)
            return false;

        DeviceAircraftBindingProfile? aircraft =
            GetActiveAircraftProfile(device);

        return aircraft?.ButtonBindings.Any(binding =>
            binding.ButtonIndex == buttonIndex &&
            DeviceButtonBinding.IsDxShiftCallback(
                binding.CallbackName)) == true;
    }

    private bool IsPhysicalDxShiftHeld()
    {
        // Inspect existing shift bindings across connected devices.
        // DirectInputCaptureHost exposes the current physical button state.
        foreach (DeviceBindingProfile device in _deviceProfiles)
        {
            if (!device.IsConnected)
                continue;

            DeviceAircraftBindingProfile? aircraft =
                GetActiveAircraftProfile(device);

            if (aircraft is null)
                continue;

            foreach (DeviceButtonBinding binding in aircraft.ButtonBindings)
            {
                if (!DeviceButtonBinding.IsDxShiftCallback(
                        binding.CallbackName))
                {
                    continue;
                }

                if (_captureHost.IsJoystickButtonPressed(
                        device.DurableDeviceKey,
                        binding.ButtonIndex))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string NormalizePovInvoke(string? invoke)
    {
        return string.Equals(invoke, PovInvokeShift, StringComparison.OrdinalIgnoreCase)
            ? PovInvokeShift
            : PovInvokeDefault;
    }

    private static string BuildPovDisplayText(int povIndex, int direction, string invoke)
    {
        string text = "POV" + (povIndex + 1) + "." + GetPovDirectionName(direction).ToUpperInvariant();

        if (string.Equals(NormalizePovInvoke(invoke), PovInvokeShift, StringComparison.OrdinalIgnoreCase))
            text += " SHIFT";

        return text;
    }

    private static string GetPovDirectionName(int direction)
    {
        return direction switch
        {
            0 => "Up",
            1 => "Up-Right",
            2 => "Right",
            3 => "Down-Right",
            4 => "Down",
            5 => "Down-Left",
            6 => "Left",
            7 => "Up-Left",
            _ => direction.ToString()
        };
    }

    public void Dispose()
    {
        _awaitingDxRow.PropertyChanged -= AwaitingDxRow_PropertyChanged;
        _captureHost.Dispose();
    }
}


/// <summary>
/// Represents one DX assignment row in the Key Mapping window.
/// Each row maintains its own Shift State, Trigger, and Clear command.
/// The final row is always listening for a new input.
/// </summary>
public sealed class DxInputRowViewModel : ViewModelBase
{
    private string _shiftState = DeviceButtonBinding.ShiftStateUnshifted;
    private string _trigger = DeviceButtonBinding.TriggerPress;

    public string DeviceKey { get; init; } = "";
    public int? ButtonIndex { get; init; }
    public int? PovIndex { get; init; }
    public int? Direction { get; init; }

    public string InputText { get; init; } = "";

    public bool IsAwaitingInput { get; init; }

    public bool IsPov => PovIndex.HasValue;

    public bool IsShiftSelectionEnabled { get; init; } = true;

    public bool IsTriggerSelectionEnabled =>
        IsShiftSelectionEnabled && !IsPov;

    public Visibility ClearVisibility =>
        IsAwaitingInput ? Visibility.Hidden : Visibility.Visible;

    public IReadOnlyList<string> ShiftOptions { get; } =
        new[]
        {
            DeviceButtonBinding.ShiftStateUnshifted,
            DeviceButtonBinding.ShiftStateShifted
        };

    public IReadOnlyList<string> TriggerOptions { get; } =
        new[]
        {
            DeviceButtonBinding.TriggerPress,
            DeviceButtonBinding.TriggerRelease
        };

    public string ShiftState
    {
        get => _shiftState;
        set => Set(ref _shiftState, value);
    }

    public string Trigger
    {
        get => _trigger;
        set => Set(ref _trigger, value);
    }

    public ICommand? ClearCommand { get; set; }
}