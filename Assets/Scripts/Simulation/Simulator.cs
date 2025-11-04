using DLS.Description;
using DLS.Game;
using System.Collections.Concurrent;
using UnityEngine;
using Jint;
using Jint.Native;
using Random = System.Random;

using System;
using NativeWebSocket;
namespace DLS.Simulation
{

    // NEW: Add this enum
    public enum SpeakerCommandType { Create, Destroy }

    // NEW: Add this struct
    public struct MainThreadSpeakerCommand
    {
        public SpeakerCommandType CommandType;
        public uint SpeakerID;
    }
    public static class Simulator
	{
        // Dictionaries to hold WebSocket instances for each chip
        // This allows multiple WebIN/WebOUT chips to exist
        public static ConcurrentDictionary<SimChip, WebSocket> webInSockets = new();
        public static ConcurrentDictionary<SimChip, WebSocket> webOutSockets = new();

        // NEW: Add this queue for main thread commands
        public static readonly ConcurrentQueue<MainThreadSpeakerCommand> SpeakerCommandQueue = new();

        // NEW: Add this buffer for frequencies (from previous step)
        public static readonly ConcurrentDictionary<uint, float> SpeakerFrequencyBuffer = new();

        // ... (your other static vars like SpeakerFrequencyBuffer) ...

        // --- NEW: Speaker ID Pool ---
        // This queue holds IDs that were used but are now free (e.g., from a deleted chip)
        private static readonly ConcurrentQueue<uint> availableSpeakerIDs = new ConcurrentQueue<uint>();
        // This is the "high-water mark" for new IDs, if the available pool is empty
        private static uint nextSpeakerID = 0;
        // Lock to make sure we don't assign the same ID twice
        private static readonly object speakerIDLock = new object();
        // --------------------------


        public static readonly ConcurrentDictionary<uint, Engine> ScriptEngines = new ConcurrentDictionary<uint, Engine>();

        public static readonly Random rng = new();
        public static int stepsPerClockTransition;
		public static int simulationFrame;
		static uint pcg_rngState;

		// When sim is first built, or whenever modified, it needs to run a less efficient pass in which the traversal order of the chips is determined
		public static bool needsOrderPass;

		// Every n frames the simulation permits some random modifications to traversal order of sequential chips (to randomize outcome of race conditions)
		public static bool canDynamicReorderThisFrame;

		static SimChip prevRootSimChip;

		// Modifications to the sim are made from the main thread, but only applied on the sim thread to avoid conflicts
		static readonly ConcurrentQueue<SimModifyCommand> modificationQueue = new();

		public static void UpdateKeyboardInputFromMainThread()
		{
			SimKeyboardHelper.RefreshInputState();
		}


		// ---- Simulation outline ----
		// 1) Forward the initial player-controlled input states to all connected pins.
		// 2) Loop over all subchips not yet processed this frame, and process them if they are ready (i.e. all input pins have received all their inputs)
		//    * Note: this means that the input pins must be aware of how many input connections they have (pins choose randomly between conflicting inputs)
		//    * Note: if a pin has zero input connections, it should be considered as always ready
		// 3) Forward the outputs of the processed subchips to their connected pins, and repeat steps 2 & 3 until no more subchips are ready for processing.
		// 4) If all subchips have now been processed, then we're done. This is not necessarily the case though, since if an input pin depends on the output of its parent chip
		//    (directly or indirectly), then it won't receive all its inputs until the chip has already been run, meaning that the chip must be processed before it is ready.
		//    In this case we process one of the remaining unprocessed (and non-ready) subchips at random, and return to step 3.
		//
		// Optimization ideas (todo):
		// * Compute lookup table for combinational chips
		// * Ignore chip if inputs are same as last frame, and no internal pins changed state last frame.
		//   (would have to make exception for chips containing things like clock or key chip, which can activate 'spontaneously')
		// * Create simplified connections network allowing only builtin chips to be processed during simulation

		public static void RunSimulationStep(SimChip rootSimChip, DevPinInstance[] inputPins)
		{
			if (rootSimChip != prevRootSimChip)
			{
				needsOrderPass = true;
				prevRootSimChip = rootSimChip;
			}

			pcg_rngState = (uint)rng.Next();
			canDynamicReorderThisFrame = simulationFrame % 100 == 0;
			simulationFrame++; //

			// Step 1) Get player-controlled input states and copy values to the sim
			foreach (DevPinInstance input in inputPins)
			{
				try
				{
					SimPin simPin = rootSimChip.GetSimPinFromAddress(input.Pin.Address);
					simPin.State.SetFromSource(input.Pin.PlayerInputState);
					input.Pin.State.SetFromSource(input.Pin.PlayerInputState);
				}
				catch (Exception)
				{
					// Possible for sim to be temporarily out of sync since running on separate threads, so just ignore failure to find pin.
				}
			}

			// Process
			if (needsOrderPass)
			{
				StepChipReorder(rootSimChip);
				needsOrderPass = false;
			}
			else
			{
				StepChip(rootSimChip);
			}
		}

		// Recursively propagate signals through this chip and its subchips
		static void StepChip(SimChip chip)
		{
			// Propagate signal from all input dev-pins to all their connected pins
			chip.Sim_PropagateInputs();

			// NOTE: subchips are assumed to have been sorted in reverse order of desired visitation
			for (int i = chip.SubChips.Length - 1; i >= 0; i--)
			{
				SimChip nextSubChip = chip.SubChips[i];

				// Every n frames (for performance reasons) the simulation permits some random modifications to the chip traversal order.
				// Here two chips may be swapped if they are not 'ready' (i.e. all inputs have not yet been received for this
				// frame; indicating that the input relies on the output). The purpose of this reordering is to allow some variety in
				// the outcomes of race-conditions (such as an SR latch having both inputs enabled, and then released).
				if (canDynamicReorderThisFrame && i > 0 && !nextSubChip.Sim_IsReady() && RandomBool())
				{
					SimChip potentialSwapChip = chip.SubChips[i - 1];
					if (!ChipTypeHelper.IsBusOriginType(potentialSwapChip.ChipType))
					{
						nextSubChip = potentialSwapChip;
						(chip.SubChips[i], chip.SubChips[i - 1]) = (chip.SubChips[i - 1], chip.SubChips[i]);
					}
				}

				if (nextSubChip.IsBuiltin) ProcessBuiltinChip(nextSubChip); // We've reached a built-in chip, so process it directly
				else StepChip(nextSubChip); // Recursively process custom chip

				// Step 3) Forward the outputs of the processed subchip to connected pins
				nextSubChip.Sim_PropagateOutputs();
			}
		}

		// Recursively propagate signals through this chip and its subchips
		// In the process, reorder all subchips based on order in which they become ready for processing (have received all their inputs)
		// Note: the order here is reversed, so those ready first will be at the end of the array
		static void StepChipReorder(SimChip chip)
		{
			chip.Sim_PropagateInputs();

			SimChip[] subChips = chip.SubChips;
			int numRemaining = subChips.Length;

			while (numRemaining > 0)
			{
				int nextSubChipIndex = ChooseNextSubChip(subChips, numRemaining);
				SimChip nextSubChip = subChips[nextSubChipIndex];

				// "Remove" the chosen subchip from remaining sub chips.
				// This is done by moving it to the end of the array and reducing the length of the span by one.
				// This also places the subchip into (reverse) order, so that the traversal order need to be determined again on the next pass.
				(subChips[nextSubChipIndex], subChips[numRemaining - 1]) = (subChips[numRemaining - 1], subChips[nextSubChipIndex]);
				numRemaining--;

				// Process chosen subchip
				if (nextSubChip.ChipType == ChipType.Custom) StepChipReorder(nextSubChip); // Recursively process custom chip
				else ProcessBuiltinChip(nextSubChip); // We've reached a built-in chip, so process it directly 

				// Step 3) Forward the outputs of the processed subchip to connected pins
				nextSubChip.Sim_PropagateOutputs();
			}
		}

		static int ChooseNextSubChip(SimChip[] subChips, int num)
		{
			bool noSubChipsReady = true;
			bool isNonBusChipRemaining = false;
			int nextSubChipIndex = -1;

			// Step 2) Loop over all subchips not yet processed this frame, and process them if they are ready
			for (int i = 0; i < num; i++)
			{
				SimChip subChip = subChips[i];
				if (subChip.Sim_IsReady())
				{
					noSubChipsReady = false;
					nextSubChipIndex = i;
					break;
				}

				isNonBusChipRemaining |= !ChipTypeHelper.IsBusOriginType(subChip.ChipType);
			}

			// Step 4) if no sub chip is ready to be processed, pick one at random (but save buses for last)
			if (noSubChipsReady)
			{
				nextSubChipIndex = rng.Next(0, num);

				// If processing in random order, save buses for last (since we must know all their inputs to display correctly)
				if (isNonBusChipRemaining)
				{
					for (int i = 0; i < num; i++)
					{
						if (!ChipTypeHelper.IsBusOriginType(subChips[nextSubChipIndex].ChipType)) break;
						nextSubChipIndex = (nextSubChipIndex + 1) % num;
					}
				}
			}

			return nextSubChipIndex;
		}

		public static bool RandomBool()
		{
			pcg_rngState = pcg_rngState * 747796405 + 2891336453;
			uint result = ((pcg_rngState >> (int)((pcg_rngState >> 28) + 4)) ^ pcg_rngState) * 277803737;
			result = (result >> 22) ^ result;
			return result < uint.MaxValue / 2;
		}


		static void ProcessBuiltinChip(SimChip chip)
		{

			switch (chip.ChipType)
			{
				// ---- Process Built-in chips ----
				case ChipType.Nand:
					{
						uint andOp = chip.InputPins[0].State.GetRawBits() & chip.InputPins[1].State.GetRawBits();
						uint nandOp = ~andOp & 1;
						chip.OutputPins[0].State.SetBit(0, nandOp);
						break;
					}
				case ChipType.Clock:
					{
						bool high = stepsPerClockTransition != 0 && ((simulationFrame / stepsPerClockTransition) & 1) == 0;
						chip.OutputPins[0].State.SetBit(0, high ? PinState.LogicHigh : PinState.LogicLow);
						break;
					}

				case ChipType.RNG:
					{
						if (chip.InputPins[0].State.GetBit(0) == PinState.LogicHigh)
						{
							bool high = RandomBool();
							chip.OutputPins[0].State.SetBit(0, high ? PinState.LogicHigh : PinState.LogicLow);
						}
						break;
					}
                case ChipType.Script:
                    {
                        // InternalState[0] = Script ID (e.g., 0 for "script_0.js")
                        // All other InternalState[1...n] is free for the script to use
                        uint scriptID = chip.InternalState[0];

                        // Try to get the cached engine from the manager
                        if (Simulator.ScriptEngines.TryGetValue(scriptID, out Jint.Engine engine))
                        {
                            try
                            {
                                // --- 1. Pass Inputs TO JavaScript ---
                                // We create simple arrays of uints
                                var jsInputs = new object[chip.InputPins.Length];
                                for (int i = 0; i < chip.InputPins.Length; i++)
                                {
                                    jsInputs[i] = chip.InputPins[i].State.GetRawBits();
                                }
                                engine.SetValue("inputs", jsInputs);

                                // Also pass the chip's internal state as "memory"
                                var jsMemory = new object[chip.InternalState.Length];
                                for (int i = 0; i < chip.InternalState.Length; i++)
                                {
                                    jsMemory[i] = chip.InternalState[i];
                                }
                                engine.SetValue("memory", jsMemory);


                                // --- 2. Execute the "update()" function ---
                                engine.Invoke("update");


                                // --- 3. Get Outputs FROM JavaScript ---
                                // We retrieve the 'outputs' array from the script's global scope
                                var jsOutputs = engine.GetValue("outputs").AsArray();
                                int outputPinCount = System.Math.Min(chip.OutputPins.Length, (int)jsOutputs.Length);

                                for (int i = 0; i < outputPinCount; i++)
                                {
                                    // Convert the JS number back to a uint
                                    chip.OutputPins[i].State.SetAllBits_NoneDisconnected((uint)jsOutputs[i].AsNumber());
                                }

                                // --- 4. Get Memory changes FROM JavaScript ---
                                var jsMemoryOut = engine.GetValue("memory").AsArray();
                                int memCount = System.Math.Min(chip.InternalState.Length, (int)jsMemoryOut.Length);
                                for (int i = 0; i < memCount; i++)
                                {
                                    chip.InternalState[i] = (uint)jsMemoryOut[i].AsNumber();
                                }
                            }
                            catch (System.Exception e)
                            {
                                // Log an error but don't crash the sim
                                Debug.Log($"Script ID {scriptID} error: {e.Message}");
                            }
                        }
                        else
                        {
                            // Engine not found (script failed to load?)
                        }
                        break;
                    }
                case ChipType.Speaker:
                    {
                        // --- Config ---
                        // InternalState[0] = Speaker ID (must match the ID in the Unity script)
                        // InputPins[0] = 8-bit MIDI Note (0-127)
                        // InputPins[1] = 1-bit Gate (HIGH = play, LOW = off)
                        // ----------------

                        uint speakerID = chip.InternalState[0];
                        uint midiNote = chip.InputPins[0].State.GetRawBits();
                        bool gate = chip.InputPins[1].State.FirstBitHigh();

                        float frequency = 0.0f;

                        if (gate && midiNote > 0)
                        {
                            // Calculate frequency from MIDI note number
                            // Formula: f = 440 * 2^((n - 69) / 12)
                            // We use System.Math.Pow since UnityEngine is not available in this file
                            frequency = (float)(440.0 * Math.Pow(2.0, (midiNote - 69.0) / 12.0));
                            //Debug.Log($"Gate is HIGH. Writing frequency: {frequency} for note {midiNote}"); // For Debug purposes
                        }

                        // Write the frequency to the buffer for the Unity script to read
                        SpeakerFrequencyBuffer[speakerID] = frequency;

                        break;
                    }

                case ChipType.WebOUT:
                    {
                        // --- State Definitions ---
                        // 0 = Disconnected
                        // 1 = Connected
                        // 2 = Connecting
                        // -------------------------

                        // Get the websocket for THIS chip, or null if it doesn't exist yet
                        webOutSockets.TryGetValue(chip, out WebSocket websocket_out);

                        // --- 1. Connection Logic ---
                        if (chip.InternalState[0] == 0 && chip.InputPins[1].State.GetBit(0) == PinState.LogicHigh)
                        {
                            chip.InternalState[0] = 2; // Set state to "Connecting"
                            Debug.Log("WebOUT State set to 2 (Connecting)...");

                            if (websocket_out != null && websocket_out.State == WebSocketState.Open)
                            {
                                websocket_out.Close();
                                Debug.Log("WebOUT already open! -> CLOSING");
                                chip.InternalState[0] = 0; // Reset to disconnected
                                return;
                            }

                            websocket_out = new WebSocket($"ws://localhost:{chip.InternalState[1]}");

                            websocket_out.OnOpen += () =>
                            {
                                Debug.Log("WebOUT Connection open!");
                                websocket_out.SendText("DLS - WebOUT Connection Initiated");
                                chip.InternalState[0] = 1; // Set state to "Connected"
                            };

                            websocket_out.OnError += (e) =>
                            {
                                Debug.Log("WebOUT Error! " + e);
                                chip.InternalState[0] = 0; // Set state back to "Disconnected"
                                webOutSockets.TryRemove(chip, out _); // Clean up dictionary
                            };

                            websocket_out.OnClose += (e) =>
                            {
                                Debug.Log("WebOUT Connection closed!");
                                chip.InternalState[0] = 0; // Set state back to "Disconnected"
                                webOutSockets.TryRemove(chip, out _); // Clean up dictionary
                            };

                            websocket_out.OnMessage += (bytes) =>
                            {
                                var sb = new System.Text.StringBuilder(8);
                                for (int i = 0; i < 8; i++)
                                {
                                    uint bitState = chip.InputPins[0].State.GetBit(7 - i);
                                    sb.Append(bitState == PinState.LogicHigh ? '1' : '0');
                                }
                                websocket_out.SendText(sb.ToString());
                            };

                            // Store this websocket instance in the dictionary
                            webOutSockets[chip] = websocket_out;
                            websocket_out.Connect();
                        }

                        // --- 2. "Update" Logic (while connected) ---
                        if (chip.InternalState[0] == 1) // Only run if "Connected"
                        {
                            if (websocket_out != null)
                            {
                                websocket_out.DispatchMessageQueue();
                            }
                        }

                        // --- 3. Disconnection Logic ---
                        if (chip.InputPins[1].State.GetBit(0) == PinState.LogicLow && (chip.InternalState[0] == 1 || chip.InternalState[0] == 2))
                        {
                            Debug.Log("WebOUT Disconnect signal received. Closing connection.");
                            chip.InternalState[0] = 0; // Set state to "Disconnected"

                            if (websocket_out != null)
                            {
                                websocket_out.Close();
                                // No need to remove from dict here, OnClose event will handle it
                            }
                        }
                        break;
                    }


                case ChipType.WebIN:
                    {
                        // --- State Definitions ---
                        // 0 = Disconnected
                        // 1 = Connected
                        // 2 = Connecting
                        // -------------------------

                        // Get the websocket for THIS chip, or null if it doesn't exist yet
                        webInSockets.TryGetValue(chip, out WebSocket websocket_in);

                        // --- 1. Connection Logic ---
                        if (chip.InternalState[0] == 0 && chip.InputPins[0].State.GetBit(0) == PinState.LogicHigh)
                        {
                            chip.InternalState[0] = 2;
                            Debug.Log("State set to 2 (Connecting)...");

                            if (websocket_in != null && websocket_in.State == WebSocketState.Open)
                            {
                                websocket_in.Close();
                                Debug.Log("Websocket already open! -> CLOSING");
                                chip.InternalState[0] = 0; // Reset to disconnected
                                return;
                            }

                            websocket_in = new WebSocket($"ws://localhost:{chip.InternalState[1]}");

                            websocket_in.OnOpen += () =>
                            {
                                Debug.Log("Connection open!");
                                websocket_in.SendText("DLS - Connection Initiated");
                                chip.InternalState[0] = 1; // Set state to "Connected"
                            };

                            websocket_in.OnError += (e) =>
                            {
                                Debug.Log("Error! " + e);
                                chip.InternalState[0] = 0; // Set state back to "Disconnected"
                                webInSockets.TryRemove(chip, out _); // Clean up dictionary
                            };

                            websocket_in.OnClose += (e) =>
                            {
                                Debug.Log("Connection closed!");
                                chip.InternalState[0] = 0; // Set state back to "Disconnected"
                                webInSockets.TryRemove(chip, out _); // Clean up dictionary
                            };

                            websocket_in.OnMessage += (bytes) =>
                            {
                                var message = System.Text.Encoding.UTF8.GetString(bytes).Trim();

                                if (message.Length >= 8)
                                {
                                    // *** BIT ORDER FIX ***
                                    // This now maps message[0] (bit 7) to chip bit 7, etc.
                                    for (int i = 0; i < 8; i++)
                                    {
                                        bool high = message[i] == '1';
                                        chip.OutputPins[0].State.SetBit(7 - i, high ? PinState.LogicHigh : PinState.LogicLow);
                                    }
                                }
                            };

                            // Store this websocket instance in the dictionary
                            webInSockets[chip] = websocket_in;
                            websocket_in.Connect();
                        }

                        // --- 2. "Update" Logic (while connected) ---
                        if (chip.InternalState[0] == 1) // Only run if "Connected"
                        {
                            if (websocket_in != null)
                            {
                                websocket_in.DispatchMessageQueue();

                                chip.InternalState[2]++;
                                if (chip.InternalState[2] > 60)
                                {
                                    websocket_in.SendText("DLS - Ping");
                                    chip.InternalState[2] = 0; // Reset the counter
                                }
                            }
                        }

                        // --- 3. Disconnection Logic ---
                        if (chip.InputPins[0].State.GetBit(0) == PinState.LogicLow && (chip.InternalState[0] == 1 || chip.InternalState[0] == 2))
                        {
                            Debug.Log("Disconnect signal received. Closing connection.");
                            chip.InternalState[0] = 0; // Set state to "Disconnected"
                            chip.OutputPins[0].State.SetBit(0, PinState.LogicDisconnected);

                            if (websocket_in != null)
                            {
                                websocket_in.Close();
                                // No need to remove from dict here, OnClose event will handle it
                            }
                        }

                        break;
                    }

                case ChipType.AdjsClock:

                    {
                        bool high = stepsPerClockTransition != 0 && ((simulationFrame / (stepsPerClockTransition * 2)) & 1) == 0;
                        uint divider = chip.InputPins[0].State.GetRawBits() + 1;
                        try
						{
                            high = stepsPerClockTransition != 0 && ((simulationFrame / (stepsPerClockTransition / divider)) & 1) == 0;

                        }

						catch
						{
                            high = stepsPerClockTransition != 0 && ((simulationFrame / (stepsPerClockTransition * 2)) & 1) == 0;
                        }
                        chip.OutputPins[0].State.SetBit(0, high ? PinState.LogicHigh : PinState.LogicLow);
                        break;
                    }
                case ChipType.Pulse:
					const int pulseDurationIndex = 0;
					const int pulseTicksRemainingIndex = 1;
					const int pulseInputOldIndex = 2;

					uint pulseInputState = chip.InputPins[0].State.GetBit(0);
					bool pulseInputHigh = pulseInputState == PinState.LogicHigh;
					uint pulseTicksRemaining = chip.InternalState[pulseTicksRemainingIndex];

					if (pulseTicksRemaining == 0)
					{
						bool isRisingEdge = pulseInputHigh && chip.InternalState[pulseInputOldIndex] == 0;
						if (isRisingEdge)
						{
							pulseTicksRemaining = chip.InternalState[pulseDurationIndex];
							chip.InternalState[pulseTicksRemainingIndex] = pulseTicksRemaining;
						}
					}

					uint pulseOutput = pulseInputState == PinState.LogicDisconnected ? PinState.LogicDisconnected : PinState.LogicLow;
					if (pulseTicksRemaining > 0)
					{
						chip.InternalState[1]--;
						pulseOutput = PinState.LogicHigh;
					}

					chip.OutputPins[0].State.SetBit(0, pulseOutput);
					chip.InternalState[pulseInputOldIndex] = pulseInputHigh ? 1u : 0;

					break;
				case ChipType.Split_4To1Bit:
				{
					SimPin in4 = chip.InputPins[0];

					for (int i = 0; i < 4; i++)
					{
						chip.OutputPins[i].State.SetBit(0, in4.State.GetBit(3 - i));
					}

					break;
				}
				case ChipType.Merge_1To4Bit:
				{
					SimPin out4 = chip.OutputPins[0];

					for (int i = 0; i < 4; i++)
					{
						uint inputState = chip.InputPins[3 - i].State.GetBit(0);
						out4.State.SetBit(i, inputState);
					}

					break;
				}
				case ChipType.Merge_1To8Bit:
					for (int i = 0; i < 8; i++)
					{
						chip.OutputPins[0].State.SetBit(i, chip.InputPins[7 - i].State.GetBit(0));
					}

					break;
				case ChipType.Merge_4To8Bit:
				{
					SimPin in4A = chip.InputPins[0];
					SimPin in4B = chip.InputPins[1];
					SimPin out8 = chip.OutputPins[0];
					out8.State.Set8BitFrom4BitSources(in4B.State, in4A.State);
					break;
				}
                case ChipType.Merge_8To16Bit:
                    {
                        SimPin in8A = chip.InputPins[0];
                        SimPin in8B = chip.InputPins[1];
                        SimPin out16 = chip.OutputPins[0];
                        out16.State.Set16BitFrom8BitSources(in8A.State, in8B.State);
                        break;
                    }
                case ChipType.Merge_16To32Bit:
                    {
                        SimPin in16A = chip.InputPins[0];
                        SimPin in16B = chip.InputPins[1];
                        SimPin out32 = chip.OutputPins[0];
                        out32.State.Set32BitFrom16BitSources(in16A.State, in16B.State);
                        break;
                    }
                case ChipType.Split_8To4Bit:
				{
					SimPin in8 = chip.InputPins[0];
					SimPin out4A = chip.OutputPins[0];
					SimPin out4B = chip.OutputPins[1];
					out4A.State.Set4BitFrom8BitSource(in8.State, false);
					out4B.State.Set4BitFrom8BitSource(in8.State, true);
					break;
				}
                case ChipType.Split_16To8Bit:
                    {
                        SimPin in8 = chip.InputPins[0];
                        SimPin out8A = chip.OutputPins[0];
                        SimPin out8B = chip.OutputPins[1];
                        out8A.State.Set8BitFrom16BitSource(in8.State, false);
                        out8B.State.Set8BitFrom16BitSource(in8.State, true);
                        break;
                    }
                case ChipType.Split_32To16Bit:
                    {
                        SimPin in32 = chip.InputPins[0];
                        SimPin out16A = chip.OutputPins[0];
                        SimPin out16B = chip.OutputPins[1];
                        out16A.State.Set16BitFrom32BitSource(in32.State, false);
                        out16B.State.Set16BitFrom32BitSource(in32.State, true);
                        break;
                    }
                case ChipType.Split_8To1Bit:
					for (int i = 0; i < 8; i++)
					{
						chip.OutputPins[i].State.SetBit(0, chip.InputPins[0].State.GetBit(7 - i));
					}

					break;
                case ChipType.TriStateBuffer:
				{
					SimPin dataPin = chip.InputPins[0];
					SimPin enablePin = chip.InputPins[1];
					SimPin outputPin = chip.OutputPins[0];

					if (enablePin.FirstBitHigh)
					{
						outputPin.State.SetFromSource(dataPin.State);
					}
					else
					{
						outputPin.State.SetBit(0, PinState.LogicDisconnected);
					}

					break;
				}
				case ChipType.Key:
				{
					bool isHeld = SimKeyboardHelper.KeyIsHeld((char)chip.InternalState[0]);
					chip.OutputPins[0].State.SetBit(0, isHeld ? PinState.LogicHigh : PinState.LogicLow);
					break;
				}
				case ChipType.DisplayRGB:
				{
					const uint addressSpace = 256;
					PinState addressPin = chip.InputPins[0].State;
					PinState redPin = chip.InputPins[1].State;
					PinState greenPin = chip.InputPins[2].State;
					PinState bluePin = chip.InputPins[3].State;
					PinState resetPin = chip.InputPins[4].State;
					PinState writePin = chip.InputPins[5].State;
					PinState refreshPin = chip.InputPins[6].State;
					PinState clockPin = chip.InputPins[7].State;

					// Detect clock rising edge
					bool clockHigh = clockPin.FirstBitHigh();
					bool isRisingEdge = clockHigh && chip.InternalState[^1] == 0;
					chip.InternalState[^1] = clockHigh ? 1u : 0;

					if (isRisingEdge)
					{
						// Clear back buffer
						if (resetPin.FirstBitHigh())
						{
							for (int i = 0; i < addressSpace; i++)
							{
								chip.InternalState[i + addressSpace] = 0;
							}
						}
						// Write to back-buffer
						else if (writePin.FirstBitHigh())
						{
							uint addressIndex = addressPin.GetRawBits() + addressSpace;
							uint data = redPin.GetRawBits() | (greenPin.GetRawBits() << 4) | (bluePin.GetRawBits() << 8);
							chip.InternalState[addressIndex] = data;
						}

						// Copy back-buffer to display buffer
						if (refreshPin.FirstBitHigh())
						{
							for (int i = 0; i < addressSpace; i++)
							{
								chip.InternalState[i] = chip.InternalState[i + addressSpace];
							}
						}
					}

					// Output current pixel colour
					uint colData = chip.InternalState[addressPin.GetRawBits()];
					chip.OutputPins[0].State.SetAllBits_NoneDisconnected((colData >> 0) & 0b1111); // red
					chip.OutputPins[1].State.SetAllBits_NoneDisconnected((colData >> 4) & 0b1111); // green
					chip.OutputPins[2].State.SetAllBits_NoneDisconnected((colData >> 8) & 0b1111); // blue

					break;
				}
				case ChipType.DisplayDot:
				{
					const uint addressSpace = 256;
					PinState addressPin = chip.InputPins[0].State;
					PinState pixelInputPin = chip.InputPins[1].State;
					PinState resetPin = chip.InputPins[2].State;
					PinState writePin = chip.InputPins[3].State;
					PinState refreshPin = chip.InputPins[4].State;
					PinState clockPin = chip.InputPins[5].State;

					// Detect clock rising edge
					bool clockHigh = clockPin.FirstBitHigh();
					bool isRisingEdge = clockHigh && chip.InternalState[^1] == 0;
					chip.InternalState[^1] = clockHigh ? 1u : 0;

					if (isRisingEdge)
					{
						// Clear back buffer
						if (resetPin.FirstBitHigh())
						{
							for (int i = 0; i < addressSpace; i++)
							{
								chip.InternalState[i + addressSpace] = 0;
							}
						}
						// Write to back-buffer
						else if (writePin.FirstBitHigh())
						{
							uint addressIndex = addressPin.GetRawBits() + addressSpace;
							uint data = pixelInputPin.GetRawBits();
							chip.InternalState[addressIndex] = data;
						}

						// Copy back-buffer to display buffer
						if (refreshPin.FirstBitHigh())
						{
							for (int i = 0; i < addressSpace; i++)
							{
								chip.InternalState[i] = chip.InternalState[i + addressSpace];
							}
						}
					}

					// Output current pixel colour
					uint pixelState = chip.InternalState[addressPin.GetRawBits()];
					chip.OutputPins[0].State.SetAllBits_NoneDisconnected(pixelState);

					break;
				}
				case ChipType.dev_Ram_8Bit:
				{
					PinState addressPin = chip.InputPins[0].State;
					PinState dataPin = chip.InputPins[1].State;
					PinState writeEnablePin = chip.InputPins[2].State;
					PinState resetPin = chip.InputPins[3].State;
					PinState clockPin = chip.InputPins[4].State;

					// Detect clock rising edge
					bool clockHigh = clockPin.FirstBitHigh();
					bool isRisingEdge = clockHigh && chip.InternalState[^1] == 0;
					chip.InternalState[^1] = clockHigh ? 1u : 0;

					// Write/Reset on rising edge
					if (isRisingEdge)
					{
						if (resetPin.FirstBitHigh())
						{
							for (int i = 0; i < 256; i++)
							{
								chip.InternalState[i] = 0;
							}
						}
						else if (writeEnablePin.FirstBitHigh())
						{
							chip.InternalState[addressPin.GetRawBits()] = dataPin.GetRawBits();
						}
					}

					// Output data at current address
					chip.OutputPins[0].State.SetAllBits_NoneDisconnected(chip.InternalState[addressPin.GetRawBits()]);

					break;
				}
				case ChipType.Rom_256x16:
				{
					const int ByteMask = 0b11111111;
					uint address = chip.InputPins[0].State.GetRawBits();
					uint data = chip.InternalState[address];
					chip.OutputPins[0].State.SetAllBits_NoneDisconnected((data >> 8) & ByteMask);
					chip.OutputPins[1].State.SetAllBits_NoneDisconnected(data & ByteMask);
					break;
				}
				// ---- Bus types ----
				default:
				{
					if (ChipTypeHelper.IsBusOriginType(chip.ChipType))
					{
						SimPin inputPin = chip.InputPins[0];
						chip.OutputPins[0].State.SetFromSource(inputPin.State);
					}

					break;
				}
			}
		}

		public static SimChip BuildSimChip(ChipDescription chipDesc, ChipLibrary library)
		{
			return BuildSimChip(chipDesc, library, -1, null);
		}

		public static SimChip BuildSimChip(ChipDescription chipDesc, ChipLibrary library, int subChipID, uint[] internalState)
		{
			SimChip simChip = BuildSimChipRecursive(chipDesc, library, subChipID, internalState);
			return simChip;
		}

		// Recursively build full representation of chip from its description for simulation.
		static SimChip BuildSimChipRecursive(ChipDescription chipDesc, ChipLibrary library, int subChipID, uint[] internalState)
		{
			// Recursively create subchips
			SimChip[] subchips = chipDesc.SubChips.Length == 0 ? Array.Empty<SimChip>() : new SimChip[chipDesc.SubChips.Length];

			for (int i = 0; i < chipDesc.SubChips.Length; i++)
			{
				SubChipDescription subchipDesc = chipDesc.SubChips[i];
				ChipDescription subchipFullDesc = library.GetChipDescription(subchipDesc.Name);
				SimChip subChip = BuildSimChipRecursive(subchipFullDesc, library, subchipDesc.ID, subchipDesc.InternalData);
				subchips[i] = subChip;
			}

			SimChip simChip = new(chipDesc, subChipID, internalState, subchips);


			// Create connections
			for (int i = 0; i < chipDesc.Wires.Length; i++)
			{
				simChip.AddConnection(chipDesc.Wires[i].SourcePinAddress, chipDesc.Wires[i].TargetPinAddress);
			}

			return simChip;
		}

		public static void AddPin(SimChip simChip, int pinID, PinBitCount bitCount, bool isInputPin)
		{
			SimModifyCommand command = new()
			{
				type = SimModifyCommand.ModificationType.AddPin,
				modifyTarget = simChip,
				simPinToAdd = new SimPin(pinID, bitCount, isInputPin, simChip),
				pinIsInputPin = isInputPin
			};
			modificationQueue.Enqueue(command);
		}

		public static void RemovePin(SimChip simChip, int pinID)
		{
			SimModifyCommand command = new()
			{
				type = SimModifyCommand.ModificationType.RemovePin,
				modifyTarget = simChip,
				removePinID = pinID
			};
			modificationQueue.Enqueue(command);
		}

		public static void AddSubChip(SimChip simChip, ChipDescription desc, ChipLibrary chipLibrary, int subChipID, uint[] subChipInternalData)
		{
			SimModifyCommand command = new()
			{
				type = SimModifyCommand.ModificationType.AddSubchip,
				modifyTarget = simChip,
				chipDesc = desc,
				lib = chipLibrary,
				subChipID = subChipID,
				subChipInternalData = subChipInternalData
			};
			modificationQueue.Enqueue(command);
		}

		public static void AddConnection(SimChip simChip, PinAddress source, PinAddress target)
		{
			SimModifyCommand command = new()
			{
				type = SimModifyCommand.ModificationType.AddConnection,
				modifyTarget = simChip,
				sourcePinAddress = source,
				targetPinAddress = target
			};
			modificationQueue.Enqueue(command);
		}

		public static void RemoveConnection(SimChip simChip, PinAddress source, PinAddress target)
		{
			SimModifyCommand command = new()
			{
				type = SimModifyCommand.ModificationType.RemoveConnection,
				modifyTarget = simChip,
				sourcePinAddress = source,
				targetPinAddress = target
			};
			modificationQueue.Enqueue(command);
		}

		public static void RemoveSubChip(SimChip simChip, int id)
		{
			SimModifyCommand command = new()
			{
				type = SimModifyCommand.ModificationType.RemoveSubChip,
				modifyTarget = simChip,
				removeSubChipID = id
			};
			modificationQueue.Enqueue(command);
		}

		// Note: this should only be called from the sim thread
		public static void ApplyModifications()
		{
			while (modificationQueue.Count > 0)
			{
				needsOrderPass = true;

				if (modificationQueue.TryDequeue(out SimModifyCommand cmd))
				{
                    if (cmd.type == SimModifyCommand.ModificationType.AddSubchip)
                    {
                        SimChip newSubChip = BuildSimChip(cmd.chipDesc, cmd.lib, cmd.subChipID, cmd.subChipInternalData);
                        cmd.modifyTarget.AddSubChip(newSubChip);

                        // --- MODIFIED ---
                        if (newSubChip.ChipType == ChipType.Speaker)
                        {
                            uint assignedID;
                            // Lock to ensure thread-safety
                            lock (speakerIDLock)
                            {
                                if (availableSpeakerIDs.TryDequeue(out uint recycledID))
                                {
                                    // We found a recycled ID, let's use it
                                    assignedID = recycledID;
                                }
                                else
                                {
                                    // No recycled IDs, use the high-water mark and increment it
                                    assignedID = nextSpeakerID;
                                    nextSpeakerID++;
                                }
                            }

                            // --- THIS IS THE KEY ---
                            // The simulation *assigns* the ID to the chip.
                            // The user no longer needs to set this in the editor.
                            newSubChip.InternalState[0] = assignedID;

                            Debug.Log($"SIMULATOR: Speaker chip created. Assigning ID {assignedID}.");

                            // Now, tell the main thread to create the speaker object
                            SpeakerCommandQueue.Enqueue(new MainThreadSpeakerCommand { CommandType = SpeakerCommandType.Create, SpeakerID = assignedID });
                        }
                        // -----------
                    }
                    else if (cmd.type == SimModifyCommand.ModificationType.RemoveSubChip)
                    {
                        // Find the chip *before* removing it
                        SimChip chipToRemove = null;
                        for (int i = 0; i < cmd.modifyTarget.SubChips.Length; i++)
                        {
                            if (cmd.modifyTarget.SubChips[i].ID == cmd.removeSubChipID) // Using .ID from last fix
                            {
                                chipToRemove = cmd.modifyTarget.SubChips[i];
                                break;
                            }
                        }

                        if (chipToRemove != null && chipToRemove.ChipType == ChipType.Speaker)
                        {
                            // It's a speaker! Get its assigned ID
                            uint speakerID = chipToRemove.InternalState[0];

                            Debug.Log($"SIMULATOR: Destroying speaker. Returning ID {speakerID} to pool.");

                            // --- NEW: Return the ID to the pool ---
                            lock (speakerIDLock)
                            {
                                availableSpeakerIDs.Enqueue(speakerID);
                            }
                            // ------------------------------------

                            // Tell the main thread to destroy the speaker object
                            SpeakerCommandQueue.Enqueue(new MainThreadSpeakerCommand { CommandType = SpeakerCommandType.Destroy, SpeakerID = speakerID });

                            // Clean up the frequency buffer
                            SpeakerFrequencyBuffer.TryRemove(speakerID, out _);
                        }

                        // Now, actually remove the chip from the sim
                        cmd.modifyTarget.RemoveSubChip(cmd.removeSubChipID);
                    }
                    else if (cmd.type == SimModifyCommand.ModificationType.AddConnection)
					{
						cmd.modifyTarget.AddConnection(cmd.sourcePinAddress, cmd.targetPinAddress);
					}
					else if (cmd.type == SimModifyCommand.ModificationType.RemoveConnection)
					{
						cmd.modifyTarget.RemoveConnection(cmd.sourcePinAddress, cmd.targetPinAddress); //
					}
					else if (cmd.type == SimModifyCommand.ModificationType.AddPin)
					{
						cmd.modifyTarget.AddPin(cmd.simPinToAdd, cmd.pinIsInputPin);
					}
					else if (cmd.type == SimModifyCommand.ModificationType.RemovePin)
					{
						cmd.modifyTarget.RemovePin(cmd.removePinID);
					}

				}
			}
		}

		public static void Reset()
		{
			simulationFrame = 0;
			modificationQueue?.Clear();
		}

		struct SimModifyCommand
		{
			public enum ModificationType
			{
				AddSubchip,
				RemoveSubChip,
				AddConnection,
				RemoveConnection,
				AddPin,
				RemovePin
			}

			public ModificationType type;
			public SimChip modifyTarget;
			public ChipDescription chipDesc;
			public ChipLibrary lib;
			public int subChipID;
			public uint[] subChipInternalData;
			public PinAddress sourcePinAddress;
			public PinAddress targetPinAddress;
			public SimPin simPinToAdd;
			public bool pinIsInputPin;
			public int removePinID;
			public int removeSubChipID;
		}
	}
}