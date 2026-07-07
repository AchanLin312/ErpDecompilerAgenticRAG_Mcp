using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ErpDecompilerAgenticRAG_Mcp.Services;

public class CacheManagerService{

    //原版本使用哈希来作为目录名的一部分，现是为了区分不同版本的dll，方便回滚，但是如果只使用dll文件的修改时间进行新旧版本对比的话应该也是可以的
    


}