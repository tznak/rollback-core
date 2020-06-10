using System;
using System.Collections.Generic;
using System.Threading;

namespace Rollback
{
    public struct SessionSettings
    {
        // How often should the game state simulate in milliseconds.
        public float UpdateInterval;
        
        /// The highest amount of ping in milliseconds a remote connection is allowed to have,
        /// Higher values increases the rollback window but requires more memory.
        public float MaxRemotePing;

        // How many simulation steps to save aka how far back can we recover a missed input.
        public int RollbackWindow => (int) (MaxRemotePing / UpdateInterval) + 2;
            
        // How much extra time to spend on a simulation step so that remote
        // connections that are behind can catch up with their simulation.
        public float DelayFactor => UpdateInterval / (1000 / UpdateInterval);
    }
    
    public struct SessionCallbacks<TGameState, TInputState>
    {
        public delegate TGameState SaveGameFn();
        public delegate void LoadGameFn(TGameState gameState);
        public delegate void SimulateGameFn(TInputState[] inputStates);
        public delegate void BroadcastInputFn(PlayerHandle player, int step, TInputState state);
            
        public SaveGameFn SaveGame;
        public LoadGameFn LoadGame;
        public SimulateGameFn SimulateGame;
        public BroadcastInputFn BroadcastInput;
    }
    
    public class Session<TGameState, TInputState>
    where TGameState  : struct
    where TInputState : struct, IEquatable<TInputState>
    {
        private struct GameState
        {
            public TGameState State;
            public int Step;
        }

        private readonly SessionSettings _settings;
        private readonly SessionCallbacks<TGameState, TInputState> _callbacks;

        private readonly Mutex _rollbackMutex;
        private readonly List<Player<TInputState>> _players;
        private RingBuffer<GameState> _savedStates;
        private TInputState[] _playerInputStates;
        
        private int _currentStep;
        private float _updateTimer;

        public Session(SessionSettings settings, SessionCallbacks<TGameState, TInputState> callbacks)
        {
            _settings = settings;
            _callbacks = callbacks;
            _rollbackMutex = new Mutex();
            _players = new List<Player<TInputState>>();
            _savedStates = new RingBuffer<GameState>(_settings.RollbackWindow);
        }

        private int FindStateIndex(int step)
        {
            var stateIndex = 0;
            for (; stateIndex < _savedStates.Length; stateIndex++)
            {
                if (_savedStates[stateIndex].Step == step) break;
            }

            return stateIndex != _savedStates.Length ? stateIndex : -1;
        }

        public void Update(float deltaTime)
        {
            _rollbackMutex.WaitOne();
            
            var latestSynchronizedStep = GetLatestSynchronizedStep();
            if (latestSynchronizedStep != _currentStep)
            {
                var stateIndex = FindStateIndex(latestSynchronizedStep);
                
                Debug.Assert(stateIndex != -1, "Can't rollback to a discarded state.");
                
                var latestSynchronizedState = _savedStates[stateIndex];
                _callbacks.LoadGame(latestSynchronizedState.State);

                var stepsToResimulate = _currentStep - latestSynchronizedStep;
                for (var stepIndex = 0; stepIndex < stepsToResimulate; stepIndex++)
                {
                    var resimulateStep = latestSynchronizedStep + stepIndex;
                    if (resimulateStep != latestSynchronizedState.Step)
                    {
                        _savedStates[resimulateStep] = new GameState
                        {
                            State = _callbacks.SaveGame(), 
                            Step = resimulateStep
                        };
                    }

                    _callbacks.SimulateGame(GetInputs(resimulateStep));
                }
            }
            
            _rollbackMutex.ReleaseMutex();

            _updateTimer = Math.Max(0, _updateTimer + deltaTime - CalculateDelay());
            
            if (_updateTimer < _settings.UpdateInterval) return;
            _updateTimer -= _settings.UpdateInterval;
            
            _savedStates[_currentStep] = new GameState
            {
                State = _callbacks.SaveGame(),
                Step = _currentStep
            };

            _callbacks.SimulateGame(GetInputs(_currentStep));
            _currentStep++;
        }

        private float CalculateDelay()
        {
            var advantage = 0f;
            
            foreach (var player in _players)
            {
                var estimatedStepAdvantage = _currentStep - player.EstimatedLocalStep;
                advantage = Math.Max(advantage, estimatedStepAdvantage);
            }

            return advantage * _settings.DelayFactor;
        }

        private int GetLatestSynchronizedStep()
        {
            var lastSynchronizedStep = _currentStep;

            foreach (var player in _players)
            {
                if (player.LastConfirmedStep == Constants.NULL_STEP) continue;
                if (player.LastConfirmedStep >= lastSynchronizedStep) continue;
                lastSynchronizedStep = player.LastConfirmedStep;
                player.LastConfirmedStep = Constants.NULL_STEP;
            }

            return lastSynchronizedStep;
        }
        
        public PlayerHandle AddPlayer(PlayerType playerType)
        {
            var player = new Player<TInputState>(playerType, _settings);
            
            _players.Add(player);
            _playerInputStates = new TInputState[_players.Count];
            
            return player.PlayerHandle;
        }

        private Player<TInputState> GetPlayer(PlayerHandle playerHandle)
        {
            foreach (var player in _players)
            {
                if (player.PlayerHandle == playerHandle) return player;
            }

            Debug.Assert(false, "Could not get Player from PlayerHandle");
            return default;
        }

        public void AddLocalInput(PlayerHandle playerHandle, TInputState inputState)
        {
            Debug.Assert(playerHandle.Type == PlayerType.Local, "Only local players can use AddLocalInput");
            
            var inputAccepted = GetPlayer(playerHandle).AddInput(_currentStep, inputState);
            if (inputAccepted) _callbacks.BroadcastInput?.Invoke(playerHandle, _currentStep, inputState);
        }
        
        public void AddRemoteInput(PlayerHandle playerHandle, int step, TInputState inputState)
        {
            Debug.Assert(playerHandle.Type == PlayerType.Remote, "Only remote players can use AddRemoteInput");
            
            var player = GetPlayer(playerHandle);
            
            _rollbackMutex.WaitOne();
            player.AddInput(step, inputState);
            _rollbackMutex.ReleaseMutex();
        }

        public void SetPing(PlayerHandle playerHandle, float ping)
        {
            Debug.Assert(ping <= _settings.MaxRemotePing, "Remote player ping exceeded allowed limit");
            GetPlayer(playerHandle).Ping = ping;
        }
        
        public float GetPing(PlayerHandle playerHandle)
        {
            return GetPlayer(playerHandle).Ping;
        }

        private TInputState[] GetInputs(int step)
        {
            for (var playerIndex = 0; playerIndex < _players.Count; playerIndex++)
            {
                _playerInputStates[playerIndex] = _players[playerIndex].GetInput(step);
            }

            return _playerInputStates;
        }
    }
}