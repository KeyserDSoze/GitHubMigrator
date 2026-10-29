using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using GitHubMigrator.Infrastructure.PostgreSql;
using Microsoft.AspNetCore.Identity;
using Microsoft.IdentityModel.Tokens;

public sealed class JwtIssuer(UserManager<ApplicationUser> users, IConfiguration configuration)
{
    public async Task<object> CreateAsync(ApplicationUser user)
    {
        var roles = await users.GetRolesAsync(user);
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.UserName ?? ""),
            new("password_changed", (!user.MustChangePassword).ToString().ToLowerInvariant())
        };
        claims.AddRange(roles.Select(role => new Claim(ClaimTypes.Role, role)));
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(configuration["Jwt:SigningKey"]!));
        var token = new JwtSecurityToken(
            issuer: configuration["Jwt:Issuer"] ?? "GitHubMigrator",
            audience: configuration["Jwt:Audience"] ?? "GitHubMigrator.Web",
            claims: claims,
            expires: DateTime.UtcNow.AddMinutes(30),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
        return new
        {
            accessToken = new JwtSecurityTokenHandler().WriteToken(token),
            mustChangePassword = user.MustChangePassword,
            username = user.UserName,
            role = roles.FirstOrDefault() ?? "user",
            expiresInSeconds = 1800
        };
    }
}
