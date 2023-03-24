using System;
using System.Diagnostics;

namespace VSIntelliSenseTweaks.Utilities
{
    internal class UnmanagedStack<T> where T : unmanaged
    {
        public T[] array;
        public int count;

        public UnmanagedStack(int initialCapacity)
        {
            this.array = new T[initialCapacity];
            this.count = 0;
        }

        public void GrowCapacity(int newCapacity)
        {
            Debug.Assert(newCapacity > count);

            var newArray = new T[newCapacity];
            Array.Copy(array, newArray, count);
            this.array = newArray;
        }

        public void Push(T value)
        {
            if (count == array.Length)
            {
                GrowCapacity(this.array.Length * 2);
            }
            array[count++] = value;
        }

        public ref T Peek()
        {
            Debug.Assert(count > 0);

            return ref array[count - 1];
        }

        public T Pop()
        {
            Debug.Assert(count > 0);

            count--;
            return array[count];
        }
    }
}
