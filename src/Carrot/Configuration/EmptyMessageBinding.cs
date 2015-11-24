namespace Carrot.Configuration
{
    public class EmptyMessageBinding : MessageBinding
    {
        internal static readonly MessageBinding Instance = new EmptyMessageBinding();

        private EmptyMessageBinding()
            : base(null, null)
        {
        }
    }
}