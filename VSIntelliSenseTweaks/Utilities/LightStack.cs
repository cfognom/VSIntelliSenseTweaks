using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Controls.Primitives;

namespace VSIntelliSenseTweaks.Utilities
{
    internal class LightStack<T> where T : unmanaged
    {
        public T[] array;
        public int count;

        public LightStack(int initialCapacity)
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

        public T Pop()
        {
            count--;
            return array[count];
        }
    }
}
