using Npgsql;
using OMMS.Models;

namespace OMMS.Db.dao;

public class ProductDao
{
    private readonly string _connectionString;

    public ProductDao(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("DefaultConnection") 
            ?? "Host=localhost;Database=omms;Username=hiro;";
    }

    public async Task<List<ProductBean>> GetAllAsync()
    {
        var list = new List<ProductBean>();
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();

        await using var cmd = new NpgsqlCommand("SELECT id, code, name, price FROM products ORDER BY id", conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        
        while (await reader.ReadAsync())
        {
            list.Add(new ProductBean
            {
                Id = reader.GetInt32(0),
                Code = reader.GetString(1),
                Name = reader.GetString(2),
                Price = reader.GetDecimal(3)
            });
        }
        return list;
    }

    public async Task AddAsync(ProductBean product)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();

        await using var cmd = new NpgsqlCommand(
            "INSERT INTO products (code, name, price) VALUES (@code, @name, @price)", conn);
        cmd.Parameters.AddWithValue("code", product.Code);
        cmd.Parameters.AddWithValue("name", product.Name);
        cmd.Parameters.AddWithValue("price", product.Price);

        await cmd.ExecuteNonQueryAsync();
    }

    public async Task UpdateAsync(ProductBean product)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();

        await using var cmd = new NpgsqlCommand(
            "UPDATE products SET code = @code, name = @name, price = @price WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", product.Id);
        cmd.Parameters.AddWithValue("code", product.Code);
        cmd.Parameters.AddWithValue("name", product.Name);
        cmd.Parameters.AddWithValue("price", product.Price);

        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DeleteAsync(int id)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();

        await using var cmd = new NpgsqlCommand("DELETE FROM products WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", id);

        await cmd.ExecuteNonQueryAsync();
    }
}