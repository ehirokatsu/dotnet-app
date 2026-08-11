using Npgsql;
using OMMS.Models;

namespace OMMS.Db.dao;

public class UserDao
{
    private readonly string _connectionString;

    public UserDao(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? "Host=localhost;Database=omms;Username=hiro;";
    }

    public async Task EnsureTableAsync()
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();

        await using var cmd = new NpgsqlCommand("""
            CREATE TABLE IF NOT EXISTS users (
                id SERIAL PRIMARY KEY,
                user_id VARCHAR(50) NOT NULL UNIQUE,
                name VARCHAR(100) NOT NULL,
                email VARCHAR(255) NOT NULL
            )
            """, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<List<UserBean>> GetAllAsync()
    {
        var list = new List<UserBean>();
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();

        await using var cmd = new NpgsqlCommand(
            "SELECT id, user_id, name, email FROM users ORDER BY id", conn);
        await using var reader = await cmd.ExecuteReaderAsync();

        while (await reader.ReadAsync())
        {
            list.Add(new UserBean
            {
                Id = reader.GetInt32(0),
                UserId = reader.GetString(1),
                Name = reader.GetString(2),
                Email = reader.GetString(3)
            });
        }

        return list;
    }

    public async Task AddAsync(UserBean user)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();

        await using var cmd = new NpgsqlCommand(
            "INSERT INTO users (user_id, name, email) VALUES (@userId, @name, @email)", conn);
        cmd.Parameters.AddWithValue("userId", user.UserId);
        cmd.Parameters.AddWithValue("name", user.Name);
        cmd.Parameters.AddWithValue("email", user.Email);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task UpdateAsync(UserBean user)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();

        await using var cmd = new NpgsqlCommand(
            "UPDATE users SET user_id = @userId, name = @name, email = @email WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", user.Id);
        cmd.Parameters.AddWithValue("userId", user.UserId);
        cmd.Parameters.AddWithValue("name", user.Name);
        cmd.Parameters.AddWithValue("email", user.Email);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DeleteAsync(int id)
    {
        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync();

        await using var cmd = new NpgsqlCommand("DELETE FROM users WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", id);
        await cmd.ExecuteNonQueryAsync();
    }
}
