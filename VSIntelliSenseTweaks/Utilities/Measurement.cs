#if DEBUG
#define MEASURE_TIME
#endif

using System;
using System.Text;
using System.Diagnostics;

namespace VSIntelliSenseTweaks.Utilities
{
    struct Measurement : IDisposable
    {
#if MEASURE_TIME
        static int depth = 0;
        static StringBuilder builder = new StringBuilder();
        static int backupLength = -1;

        Stopwatch watch;
        int insertPos;
#endif
        public Measurement(string name) : this()
        {
#if MEASURE_TIME
            builder.AppendLine();
            for (int i = 0; i < depth; i++)
            {
                builder.Append("|   ");
            }
            builder.Append("'");
            builder.Append(name);
            builder.Append("' ms: ");
            insertPos = builder.Length;
            backupLength = builder.Length;
            builder.AppendLine();
            for (int i = 0; i < depth; i++)
            {
                builder.Append("|   ");
            }
            builder.Append("{");
            depth++;
            watch = Stopwatch.StartNew();
#endif
        }

        public void Dispose()
        {
#if MEASURE_TIME
            var ms = watch.ElapsedMilliseconds;
            depth--;
            if (backupLength >= 0)
            {
                builder.Length = backupLength;
                backupLength = -1;
            }
            else
            {
                builder.AppendLine();
                for (int i = 0; i < depth; i++)
                {
                    builder.Append("|   ");
                }
                builder.Append("}");
            }
            builder.Insert(insertPos, ms.ToString());

            if (depth == 0)
            {
                Debug.WriteLine(builder.ToString());
                builder.Clear();
            }
#endif
        }
    }
}