using System;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;

namespace PagefileMonitor
{
    public static class PipeProtocol
    {
        public const string Name="PagefileMonitor.v1";
        static async Task ReadExact(Stream stream,byte[] bytes)
        {
            int offset=0;
            while(offset<bytes.Length)
            {
                var read=stream.ReadAsync(bytes,offset,bytes.Length-offset);
                if(await Task.WhenAny(read,Task.Delay(10000))!=read)throw new TimeoutException("Local request timed out");
                int count=await read;if(count==0)throw new EndOfStreamException();offset+=count;
            }
        }
        public static async Task<string> Read(Stream stream,int maximum)
        {
            var length=new byte[4];await ReadExact(stream,length);int n=BitConverter.ToInt32(length,0);
            if(n<0 || n>maximum)throw new InvalidDataException("Invalid local message length");
            var bytes=new byte[n];await ReadExact(stream,bytes);return Encoding.UTF8.GetString(bytes);
        }
        public static async Task Write(Stream stream,string value)
        {
            byte[] bytes=Encoding.UTF8.GetBytes(value);if(bytes.Length>8*1024*1024)throw new InvalidDataException("Message too large");
            byte[] length=BitConverter.GetBytes(bytes.Length);
            await WithTimeout(stream.WriteAsync(length,0,length.Length));await WithTimeout(stream.WriteAsync(bytes,0,bytes.Length));await WithTimeout(stream.FlushAsync());
        }
        static async Task WithTimeout(Task task)
        {if(await Task.WhenAny(task,Task.Delay(10000))!=task)throw new TimeoutException("Local response timed out");await task;}
        public static Reply Send(Request request)
        {
            using(var pipe=new NamedPipeClientStream(".",Name,PipeDirection.InOut,PipeOptions.Asynchronous,TokenImpersonationLevel.Impersonation))
            {
                pipe.Connect(2500);
                Write(pipe,Json.Write(request)).GetAwaiter().GetResult();
                return Json.Read<Reply>(Read(pipe,8*1024*1024).GetAwaiter().GetResult());
            }
        }
    }
}
