namespace Rollback
{
    public struct RingBuffer<TElement>
    {
        private readonly TElement[] _elements;
        private int _lastSetIndex;

        public int Length => _elements.Length;

        public RingBuffer(int capacity)
        {
            _elements = new TElement[capacity];
            _lastSetIndex = -1;
        }

        private int LoopedIndex(int index)
        {
            return (index < 0 ? _elements.Length - index : index) % _elements.Length;
        }
        
        public TElement this[int index]
        {
            get => _elements[LoopedIndex(index)];
            set
            {
                _lastSetIndex = LoopedIndex(index);
                _elements[_lastSetIndex] = value;
            }
        }
    }
}