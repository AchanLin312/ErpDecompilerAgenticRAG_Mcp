
using Microsoft.EntityFrameworkCore;
using ErpDecompilerAgenticRAG_Mcp.Models;

namespace ErpDecompilerAgenticRAG_Mcp.Data;

public class ErpDbContext : DbContext
{
    public DbSet<TypeRecord> Types { get; set; } //相当于用类来定义表结构，如果数据库中没有这个表也会自动创建，相当于之前写的CREATE TABLE语句
    public DbSet<AssemblyMetadata> AssembliesMetadata { get; set; }

    public ErpDbContext(DbContextOptions<ErpDbContext> options)
        : base(options)
    {
    }

    // 添加索引，相当于之前在IndexDatabaseService中的类似于CREATE INDEX IF NOT EXISTS idx_assembly_name ON Types(AssemblyName);的sql
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {

        modelBuilder.Entity<TypeRecord>()
            .HasIndex(t => t.AssemblyKey);

        modelBuilder.Entity<TypeRecord>()
            .HasIndex(t => t.TypeKind);
    }
}