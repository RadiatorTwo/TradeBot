using ClaudeTradingBot.Services;
using ClaudeTradingBot.Tests.Helpers;
using FluentAssertions;

namespace ClaudeTradingBot.Tests;

public class AuthServiceTests
{
    private AuthService CreateService(string dbName)
        => new(new TestDbContextFactory(dbName));

    // ── CreateUser ──────────────────────────────────────────────────────

    [Fact]
    public async Task CreateUser_ValidInput_ReturnsUser()
    {
        var svc = CreateService(nameof(CreateUser_ValidInput_ReturnsUser));

        var user = await svc.CreateUserAsync("testuser", "password123");

        user.Username.Should().Be("testuser");
        user.Id.Should().BeGreaterThan(0);
        user.MustChangePassword.Should().BeFalse();
    }

    [Fact]
    public async Task CreateUser_DuplicateUsername_ThrowsInvalidOperation()
    {
        var svc = CreateService(nameof(CreateUser_DuplicateUsername_ThrowsInvalidOperation));

        await svc.CreateUserAsync("duplicate", "password123");

        var act = () => svc.CreateUserAsync("duplicate", "otherpass123");
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*bereits vergeben*");
    }

    [Fact]
    public async Task CreateUser_PasswordIsHashed_NotStoredPlain()
    {
        var svc = CreateService(nameof(CreateUser_PasswordIsHashed_NotStoredPlain));

        var user = await svc.CreateUserAsync("hashtest", "mysecretpassword");

        user.PasswordHash.Should().NotBe("mysecretpassword");
        user.PasswordHash.Should().StartWith("$2");
    }

    [Fact]
    public async Task CreateUser_MustChangePassword_SetsFlag()
    {
        var svc = CreateService(nameof(CreateUser_MustChangePassword_SetsFlag));

        var user = await svc.CreateUserAsync("flaguser", "password123", mustChangePassword: true);

        user.MustChangePassword.Should().BeTrue();
    }

    // ── ValidateCredentials ─────────────────────────────────────────────

    [Fact]
    public async Task ValidateCredentials_CorrectPassword_ReturnsUser()
    {
        var svc = CreateService(nameof(ValidateCredentials_CorrectPassword_ReturnsUser));
        await svc.CreateUserAsync("valid", "correct123");

        var result = await svc.ValidateCredentialsAsync("valid", "correct123");

        result.Should().NotBeNull();
        result!.Username.Should().Be("valid");
    }

    [Fact]
    public async Task ValidateCredentials_WrongPassword_ReturnsNull()
    {
        var svc = CreateService(nameof(ValidateCredentials_WrongPassword_ReturnsNull));
        await svc.CreateUserAsync("wrongpw", "correct123");

        var result = await svc.ValidateCredentialsAsync("wrongpw", "wrong123");

        result.Should().BeNull();
    }

    [Fact]
    public async Task ValidateCredentials_NonExistentUser_ReturnsNull()
    {
        var svc = CreateService(nameof(ValidateCredentials_NonExistentUser_ReturnsNull));

        var result = await svc.ValidateCredentialsAsync("ghost", "password");

        result.Should().BeNull();
    }

    // ── ChangePassword ──────────────────────────────────────────────────

    [Fact]
    public async Task ChangePassword_ValidUser_UpdatesHashAndClearsFlag()
    {
        var svc = CreateService(nameof(ChangePassword_ValidUser_UpdatesHashAndClearsFlag));
        var user = await svc.CreateUserAsync("changepw", "oldpass123", mustChangePassword: true);

        await svc.ChangePasswordAsync(user.Id, "newpass456");

        var updated = await svc.GetUserByIdAsync(user.Id);
        updated!.MustChangePassword.Should().BeFalse();

        var validated = await svc.ValidateCredentialsAsync("changepw", "newpass456");
        validated.Should().NotBeNull();

        var oldValidated = await svc.ValidateCredentialsAsync("changepw", "oldpass123");
        oldValidated.Should().BeNull();
    }

    [Fact]
    public async Task ChangePassword_InvalidUser_ThrowsInvalidOperation()
    {
        var svc = CreateService(nameof(ChangePassword_InvalidUser_ThrowsInvalidOperation));

        var act = () => svc.ChangePasswordAsync(9999, "newpass");
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    // ── DeleteUser ──────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteUser_CannotDeleteSelf()
    {
        var svc = CreateService(nameof(DeleteUser_CannotDeleteSelf));
        var user = await svc.CreateUserAsync("selfdelete", "pass1234");

        var act = () => svc.DeleteUserAsync(user.Id, user.Id);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*selbst*");
    }

    [Fact]
    public async Task DeleteUser_LastUser_ThrowsInvalidOperation()
    {
        var svc = CreateService(nameof(DeleteUser_LastUser_ThrowsInvalidOperation));
        var user1 = await svc.CreateUserAsync("onlyone", "pass1234");
        var user2 = await svc.CreateUserAsync("deleter", "pass1234");

        // Delete user1 first (by user2) - should work
        await svc.DeleteUserAsync(user1.Id, user2.Id);

        // Now try to delete user2 (last user) - should fail
        var act = () => svc.DeleteUserAsync(user2.Id, 9999);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*letzte*");
    }

    [Fact]
    public async Task DeleteUser_ValidUser_RemovesFromDb()
    {
        var svc = CreateService(nameof(DeleteUser_ValidUser_RemovesFromDb));
        var user1 = await svc.CreateUserAsync("keeper", "pass1234");
        var user2 = await svc.CreateUserAsync("todelete", "pass1234");

        await svc.DeleteUserAsync(user2.Id, user1.Id);

        var deleted = await svc.GetUserByIdAsync(user2.Id);
        deleted.Should().BeNull();
        (await svc.GetUserCountAsync()).Should().Be(1);
    }

    // ── ResetPassword ───────────────────────────────────────────────────

    [Fact]
    public async Task ResetPassword_SetsMustChangePasswordTrue()
    {
        var svc = CreateService(nameof(ResetPassword_SetsMustChangePasswordTrue));
        var user = await svc.CreateUserAsync("resetuser", "oldpass12");

        await svc.ResetPasswordAsync(user.Id, "newpass12");

        var updated = await svc.GetUserByIdAsync(user.Id);
        updated!.MustChangePassword.Should().BeTrue();

        var validated = await svc.ValidateCredentialsAsync("resetuser", "newpass12");
        validated.Should().NotBeNull();
    }

    // ── UpdateLastLogin ─────────────────────────────────────────────────

    [Fact]
    public async Task UpdateLastLogin_SetsTimestamp()
    {
        var svc = CreateService(nameof(UpdateLastLogin_SetsTimestamp));
        var user = await svc.CreateUserAsync("loginuser", "pass1234");
        user.LastLoginAt.Should().BeNull();

        await svc.UpdateLastLoginAsync(user.Id);

        var updated = await svc.GetUserByIdAsync(user.Id);
        updated!.LastLoginAt.Should().NotBeNull();
        updated.LastLoginAt.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task UpdateLastLogin_NonExistentUser_DoesNotThrow()
    {
        var svc = CreateService(nameof(UpdateLastLogin_NonExistentUser_DoesNotThrow));

        var act = () => svc.UpdateLastLoginAsync(9999);
        await act.Should().NotThrowAsync();
    }

    // ── GetAllUsers / GetUserCount ──────────────────────────────────────

    [Fact]
    public async Task GetAllUsers_ReturnsOrderedList()
    {
        var svc = CreateService(nameof(GetAllUsers_ReturnsOrderedList));
        await svc.CreateUserAsync("zulu", "pass1234");
        await svc.CreateUserAsync("alpha", "pass1234");
        await svc.CreateUserAsync("mike", "pass1234");

        var users = await svc.GetAllUsersAsync();

        users.Should().HaveCount(3);
        users[0].Username.Should().Be("alpha");
        users[1].Username.Should().Be("mike");
        users[2].Username.Should().Be("zulu");
    }
}
