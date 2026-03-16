using ClaudeTradingBot.Data;
using ClaudeTradingBot.Models;
using Microsoft.EntityFrameworkCore;

namespace ClaudeTradingBot.Services;

public interface IAuthService
{
    Task<AppUser?> ValidateCredentialsAsync(string username, string password);
    Task<AppUser> CreateUserAsync(string username, string password, bool mustChangePassword = false);
    Task ChangePasswordAsync(int userId, string newPassword);
    Task<List<AppUser>> GetAllUsersAsync();
    Task<AppUser?> GetUserByIdAsync(int id);
    Task DeleteUserAsync(int userId, int currentUserId);
    Task ResetPasswordAsync(int userId, string newPassword);
    Task UpdateLastLoginAsync(int userId);
    Task<int> GetUserCountAsync();
}

public class AuthService : IAuthService
{
    private readonly IDbContextFactory<TradingDbContext> _dbFactory;

    public AuthService(IDbContextFactory<TradingDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<AppUser?> ValidateCredentialsAsync(string username, string password)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var user = await db.AppUsers.FirstOrDefaultAsync(u => u.Username == username);
        if (user is null)
            return null;

        if (!BCrypt.Net.BCrypt.EnhancedVerify(password, user.PasswordHash))
            return null;

        return user;
    }

    public async Task<AppUser> CreateUserAsync(string username, string password, bool mustChangePassword = false)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();

        var exists = await db.AppUsers.AnyAsync(u => u.Username == username);
        if (exists)
            throw new InvalidOperationException($"Benutzername '{username}' ist bereits vergeben.");

        var user = new AppUser
        {
            Username = username,
            PasswordHash = BCrypt.Net.BCrypt.EnhancedHashPassword(password, 12),
            MustChangePassword = mustChangePassword,
            CreatedAt = DateTime.UtcNow
        };

        db.AppUsers.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    public async Task ChangePasswordAsync(int userId, string newPassword)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var user = await db.AppUsers.FindAsync(userId)
            ?? throw new InvalidOperationException("Benutzer nicht gefunden.");

        user.PasswordHash = BCrypt.Net.BCrypt.EnhancedHashPassword(newPassword, 12);
        user.MustChangePassword = false;
        await db.SaveChangesAsync();
    }

    public async Task<List<AppUser>> GetAllUsersAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.AppUsers.OrderBy(u => u.Username).ToListAsync();
    }

    public async Task<AppUser?> GetUserByIdAsync(int id)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.AppUsers.FindAsync(id);
    }

    public async Task DeleteUserAsync(int userId, int currentUserId)
    {
        if (userId == currentUserId)
            throw new InvalidOperationException("Sie koennen sich nicht selbst loeschen.");

        await using var db = await _dbFactory.CreateDbContextAsync();

        var count = await db.AppUsers.CountAsync();
        if (count <= 1)
            throw new InvalidOperationException("Der letzte Benutzer kann nicht geloescht werden.");

        var user = await db.AppUsers.FindAsync(userId)
            ?? throw new InvalidOperationException("Benutzer nicht gefunden.");

        db.AppUsers.Remove(user);
        await db.SaveChangesAsync();
    }

    public async Task ResetPasswordAsync(int userId, string newPassword)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var user = await db.AppUsers.FindAsync(userId)
            ?? throw new InvalidOperationException("Benutzer nicht gefunden.");

        user.PasswordHash = BCrypt.Net.BCrypt.EnhancedHashPassword(newPassword, 12);
        user.MustChangePassword = true;
        await db.SaveChangesAsync();
    }

    public async Task UpdateLastLoginAsync(int userId)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var user = await db.AppUsers.FindAsync(userId);
        if (user is not null)
        {
            user.LastLoginAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }
    }

    public async Task<int> GetUserCountAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        return await db.AppUsers.CountAsync();
    }
}
