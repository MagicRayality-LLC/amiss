namespace Amiss.Shell
{
    /// <summary>
    /// What the hub button does for the current room check.
    /// </summary>
    public enum EpisodeHubAction
    {
        None,
        Primary,
        Launch,
        StartTrial,
        AddMissingWalls,
        RedrawRoom,
        RetrySetup
    }

    /// <summary>
    /// Decides whether episode one can launch. A room scan has to load
    /// first. A failed check names the action that resolves it. A loaded scan
    /// confirms the room on its own press; the trial starts on a later press,
    /// by the wall knife switch or the hub's START TRIAL. Later cards stay
    /// locked either way.
    /// </summary>
    public readonly struct EpisodeLaunchGate
    {
        public bool CanLaunchEpisodeOne { get; }
        public bool ButtonEnabled { get; }
        public bool ScanReady { get; }
        public string Label { get; }
        public string Status { get; }
        public EpisodeHubAction Action { get; }
        public string SecondaryLabel { get; }
        public bool EmphasizeStatus { get; }

        public EpisodeLaunchGate(bool canLaunchEpisodeOne, bool buttonEnabled, bool scanReady,
            string label, string status, EpisodeHubAction action = EpisodeHubAction.None,
            string secondaryLabel = "", bool emphasizeStatus = false)
        {
            CanLaunchEpisodeOne = canLaunchEpisodeOne;
            ButtonEnabled = buttonEnabled;
            ScanReady = scanReady;
            Label = label;
            Status = status;
            Action = action;
            SecondaryLabel = secondaryLabel ?? "";
            EmphasizeStatus = emphasizeStatus;
        }

        /// <summary>
        /// The hub stays up through room confirmation. It closes once
        /// episode one has actually started.
        /// </summary>
        public static bool HubClosesAfterEpisodeStart(ShellSessionState session) =>
            session == ShellSessionState.SetupAccepted;

        /// <summary>
        /// The wall set — television, speaker, and knife switch — appears as
        /// soon as the room is confirmed and stays through the run. The hub
        /// is a separate surface, so an open hub does not hide the wall.
        /// </summary>
        public static bool WallSetVisible(ShellSessionState session) =>
            session == ShellSessionState.RoomConfirmed || session == ShellSessionState.SetupAccepted;

        /// <summary>
        /// A pinch closes the knife switch. Only a confirmed room that has not
        /// accepted the setup may start, so an unconfirmed room, a running
        /// trial, and an already closed blade change nothing.
        /// </summary>
        public static bool KnifeSwitchStartsTrial(ShellSessionState session, bool closed) =>
            !closed && session == ShellSessionState.RoomConfirmed;

        /// <summary>True once the trial has accepted its setup and is running.</summary>
        public static bool TrialRunning(ShellSessionState session) =>
            session == ShellSessionState.SetupAccepted;

        /// <summary>
        /// A second activation of the switch ends the running trial. This is the
        /// same switch that starts it, so the stop is allowed only while the
        /// session reports the trial running.
        /// </summary>
        public static bool KnifeSwitchStopsTrial(ShellSessionState session) =>
            TrialRunning(session);

        /// <summary>
        /// The stop also needs a live runtime Trial to release. A running
        /// session without one is refused rather than faked, and that refusal
        /// leaves the session running.
        /// </summary>
        public static bool CanStopTrial(ShellSessionState session, bool runtimeTrialRunning) =>
            KnifeSwitchStopsTrial(session) && runtimeTrialRunning;

        public static string StopRefusalReason(ShellSessionState session, bool runtimeTrialRunning)
        {
            if (!KnifeSwitchStopsTrial(session))
                return "not_running";
            return runtimeTrialRunning ? "" : "no_runtime_trial";
        }

        public static bool ScanLoaded(ShellSessionState session, Room.RoomPreparationStatus room) =>
            room == Room.RoomPreparationStatus.AwaitingConfirmation ||
            room == Room.RoomPreparationStatus.Confirmed ||
            session == ShellSessionState.RoomConfirmed ||
            session == ShellSessionState.SetupAccepted;

        public static EpisodeLaunchGate Evaluate(bool episodeOpen, ShellSessionState session,
            Room.RoomPreparationStatus room, Room.RoomFailureReason failure = Room.RoomFailureReason.None,
            string boundaryLabel = "", string startBlock = "", bool safetyCheck = true)
        {
            var scanReady = ScanLoaded(session, room);
            if (!episodeOpen)
            {
                return new EpisodeLaunchGate(false, false, scanReady,
                    "LOCKED", "This episode is locked.");
            }

            if (session == ShellSessionState.StartBlocked)
            {
                return new EpisodeLaunchGate(false, false, scanReady,
                    "CANNOT LOAD", "This episode cannot start.");
            }

            if (session == ShellSessionState.SetupAccepted)
            {
                return new EpisodeLaunchGate(false, false, true,
                    "EPISODE ONE", "Episode one is running.");
            }

            if (scanReady)
                return Loaded(room, failure, boundaryLabel, startBlock, safetyCheck);

            switch (room)
            {
                case Room.RoomPreparationStatus.PermissionRequested:
                    return Waiting(scanReady, "WAITING FOR PERMISSION",
                        "Accept the scene-access prompt on the headset. The scan stays on this headset.");
                case Room.RoomPreparationStatus.SpaceSetupRequested:
                    return Waiting(scanReady, "COMPLETE SPACE SETUP",
                        "Finish Space Setup. Draw the floor and the walls, then stay in that room until it loads.");
                case Room.RoomPreparationStatus.SceneLoading:
                    return Waiting(scanReady, "LOADING ROOM",
                        "Loading the room saved on this headset. Stay inside it.");
                case Room.RoomPreparationStatus.Explanation:
                    return Scan("ALLOW SCENE ACCESS",
                        "Scene data stays on this headset. Press ALLOW SCENE ACCESS and accept the prompt.");
                case Room.RoomPreparationStatus.SpaceSetupReady:
                    return Guide("START SPACE SETUP", SpaceSetupGuide(failure),
                        EpisodeHubAction.Primary, false);
                case Room.RoomPreparationStatus.Restricted:
                    return Restricted(failure);
                default:
                    return Scan("LAUNCH",
                        "Press LAUNCH once. This loads the room saved on this headset and confirms it. Then close the knife switch on the wall, or press START TRIAL, to start episode one. Allow scene access if the headset asks, and stay inside the room.");
            }
        }

        static EpisodeLaunchGate Loaded(Room.RoomPreparationStatus room, Room.RoomFailureReason failure,
            string boundaryLabel, string startBlock, bool safetyCheck)
        {
            if (room == Room.RoomPreparationStatus.Confirmed && !string.IsNullOrEmpty(startBlock))
                return TrialStartGuide(startBlock);

            // The room is confirmed, so this press accepts the setup and starts
            // episode one. The press that confirmed the room came first.
            if (room == Room.RoomPreparationStatus.Confirmed)
                return new EpisodeLaunchGate(true, true, true, "START TRIAL",
                    "The room is confirmed. Close the knife switch on the wall, or press START TRIAL here, to start episode one.",
                    EpisodeHubAction.StartTrial, "", false);

            switch (failure)
            {
                case Room.RoomFailureReason.None:
                    return new EpisodeLaunchGate(true, true, true, "LAUNCH", LaunchStatus(boundaryLabel),
                        EpisodeHubAction.Launch, "", VirtualBoundary(boundaryLabel));
                case Room.RoomFailureReason.DisconnectedRegion:
                    return Guide("ADD MISSING WALLS",
                        "This scan is missing a floor, or it has fewer than three walls. Press ADD MISSING WALLS. Those added edges are virtual walls on this scan, not a new room. Then press LAUNCH to confirm the room.",
                        EpisodeHubAction.AddMissingWalls);
                case Room.RoomFailureReason.BelowMinimum:
                    return Guide("REDRAW THE ROOM",
                        "This room is under 3 m³, so it is too small to start. Press REDRAW THE ROOM and draw a larger room, including the floor and the walls.",
                        EpisodeHubAction.RedrawRoom);
                case Room.RoomFailureReason.UnsafeRegion:
                    if (!safetyCheck)
                        return new EpisodeLaunchGate(true, true, true, "LAUNCH",
                            "Safety check is off. LAUNCH confirms this room even though the open space around you is under 1.8 m.",
                            EpisodeHubAction.Launch, "", false);
                    return new EpisodeLaunchGate(true, true, true, "LAUNCH",
                        "Safety hold: the open space around you is under 1.8 m. A wall or a piece of furniture is inside that space. Step into a clearer spot, then press LAUNCH to confirm this room. Or turn SAFETY off.",
                        EpisodeHubAction.Launch, "", true);
                default:
                    return Guide("SCAN AGAIN",
                        "This scan cannot be used. Stand inside the room and press SCAN AGAIN. Include the floor and at least three walls.",
                        EpisodeHubAction.RetrySetup, true);
            }
        }

        static EpisodeLaunchGate TrialStartGuide(string startBlock)
        {
            switch (startBlock)
            {
                case "below_minimum_volume":
                    return Guide("REDRAW THE ROOM",
                        "This room is under 3 m³, so it is too small to start. Press REDRAW THE ROOM and draw a larger room.",
                        EpisodeHubAction.RedrawRoom);
                case "invalid_volume":
                    return Guide("SCAN AGAIN",
                        "The room measurement cannot be used. Stand inside the room and press SCAN AGAIN.",
                        EpisodeHubAction.RetrySetup);
                default:
                    return new EpisodeLaunchGate(true, true, true, "START TRIAL",
                        "The room could not be locked for this run. Step away from walls and furniture, then press START TRIAL, or close the knife switch again.",
                        EpisodeHubAction.StartTrial, "", true);
            }
        }

        static EpisodeLaunchGate Restricted(Room.RoomFailureReason failure)
        {
            switch (failure)
            {
                case Room.RoomFailureReason.ScenePermission:
                    return Guide("ALLOW SCENE ACCESS",
                        "Scene access was denied, so the room cannot load. Press ALLOW SCENE ACCESS and accept the headset prompt. The scan stays on this headset.",
                        EpisodeHubAction.RetrySetup, false);
                case Room.RoomFailureReason.SpaceSetupCancelled:
                    return Guide("REDRAW THE ROOM",
                        "Space Setup was cancelled, so this room was not saved. Press REDRAW THE ROOM. Draw the floor and the walls, then stay in the room until it loads.",
                        EpisodeHubAction.RedrawRoom, false);
                case Room.RoomFailureReason.SpaceSetupFailed:
                    return Guide("REDRAW THE ROOM",
                        "Space Setup did not finish. Press REDRAW THE ROOM and draw the floor and the walls of the room you are in.",
                        EpisodeHubAction.RedrawRoom, false);
                case Room.RoomFailureReason.NoCurrentRoom:
                    return Guide("REDRAW THE ROOM",
                        "The saved room does not contain where you are standing. Press REDRAW THE ROOM and draw this room. A room from another place is not used.",
                        EpisodeHubAction.RedrawRoom, false);
                case Room.RoomFailureReason.NoScene:
                    return Guide("REDRAW THE ROOM",
                        "No saved room matches where you are standing. Press REDRAW THE ROOM and draw the floor and the walls around you.",
                        EpisodeHubAction.RedrawRoom, false);
                default:
                    return Guide("SCAN AGAIN",
                        "This room cannot be used. Stand inside it and press SCAN AGAIN. Include the floor and at least three walls.",
                        EpisodeHubAction.RetrySetup, false);
            }
        }

        static string SpaceSetupGuide(Room.RoomFailureReason failure)
        {
            if (failure == Room.RoomFailureReason.NoCurrentRoom)
                return "The saved room does not contain where you are standing. Press START SPACE SETUP and draw this room. A room from another place is not used.";
            return "No room is saved for where you are standing. Press START SPACE SETUP. Draw the floor and the walls, and stay inside that room until it finishes.";
        }

        static string LaunchStatus(string boundaryLabel)
        {
            const string start = " Then close the knife switch, or press START TRIAL, to start episode one.";
            if (string.IsNullOrEmpty(boundaryLabel))
                return "Room scan is done. Press LAUNCH to confirm this room." + start;
            if (boundaryLabel.IndexOf("not the scanned room", System.StringComparison.Ordinal) >= 0)
                return "These are virtual boundaries, not the scanned room. Press LAUNCH to confirm this room inside this box." +
                       start;
            if (boundaryLabel.IndexOf("virtual supplements", System.StringComparison.Ordinal) >= 0)
                return "Missing edges were added as virtual boundaries, not scanned walls. Press LAUNCH to confirm this room." +
                       start;
            return "Room scan is done. Press LAUNCH to confirm this room." + start;
        }

        static bool VirtualBoundary(string boundaryLabel) =>
            !string.IsNullOrEmpty(boundaryLabel) &&
            boundaryLabel.IndexOf("virtual", System.StringComparison.Ordinal) >= 0;

        static EpisodeLaunchGate Guide(string label, string status, EpisodeHubAction action,
            bool scanReady = true) =>
            new EpisodeLaunchGate(false, true, scanReady, label, status, action, "", true);

        static EpisodeLaunchGate Waiting(bool scanReady, string label, string status) =>
            new EpisodeLaunchGate(false, false, scanReady, label, status, EpisodeHubAction.None, "", true);

        static EpisodeLaunchGate Scan(string label, string status) =>
            new EpisodeLaunchGate(false, true, false, label, status, EpisodeHubAction.Primary, "", true);
    }
}
