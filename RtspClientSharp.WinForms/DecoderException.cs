using System;

namespace RtspClientSharp.WinForms
{
    public sealed class DecoderException : Exception
    {
        public DecoderException(string message)
            : base(message)
        {
        }

        public DecoderException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }
}
