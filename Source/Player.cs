using System;

namespace Rollback
{
    public enum PlayerType
    {
        Local,
        Remote,
        Spectator
    }
    
    public readonly struct PlayerHandle
    {
        public readonly int Id;
        public readonly PlayerType Type;

        public PlayerHandle(int id, PlayerType type)
        {
            Id = id;
            Type = type;
        }

        public static bool operator ==(PlayerHandle a, PlayerHandle b) => a.Id == b.Id;
        public static bool operator !=(PlayerHandle a, PlayerHandle b) => a.Id != b.Id;
    }
    
    public class Player<TState>
    where TState : struct, IEquatable<TState>
    {
        private struct InputState
        {
            public TState State;
            public int Step;
        }

        private readonly SessionSettings _settings;
        private RingBuffer<InputState> _savedStates;
        private int _lastAddedStep;

        public PlayerHandle PlayerHandle { get; }
        public int LastConfirmedStep { get; set; }
        public float Ping { get; set; }

        public int EstimatedLocalStep => _lastAddedStep + (int) (Ping / _settings.UpdateInterval);

        internal Player(PlayerType playerType, SessionSettings settings)
        {
            _settings = settings;
            PlayerHandle = new PlayerHandle(Guid.NewGuid().GetHashCode(), playerType);
            _savedStates = new RingBuffer<InputState>(_settings.RollbackWindow);
            LastConfirmedStep = Constants.NULL_STEP;
            _lastAddedStep = Constants.NULL_STEP;
        }

        public bool AddInput(int step, TState state)
        {
            // If input already registered for this step, ignore it
            if (step <= _lastAddedStep) return false;

            Debug.Assert(step == _lastAddedStep + 1, "Steps has to be added sequentially");

            _lastAddedStep = step;
            _savedStates[step] = new InputState {State = state, Step = step};

            if (PlayerHandle.Type == PlayerType.Remote
            &&  LastConfirmedStep == Constants.NULL_STEP
            &&  !_savedStates[step - 1].State.Equals(state))
            {
                LastConfirmedStep = step;
            }

            return true;
        }

        public TState GetInput(int step)
        {
            step = Math.Max(0, Math.Min(step, _lastAddedStep));

            var input = _savedStates[step];
            
            Debug.Assert(input.Step == step, "Tried to get Input from a discarded step.");
            
            return input.State;
        }
    }
}