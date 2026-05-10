import re

file_path = r'C:\Users\ash12\RiderProjects\pi stream cam\pi stream cam\Program.cs'
with open(file_path, 'r', encoding='utf-8') as f:
    content = f.read()

# Update CORS to allow credentials
old_cors = '''builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});'''

new_cors = '''builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
    options.AddPolicy("AllowApp", policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials();
    });
});'''

content = content.replace(old_cors, new_cors)

with open(file_path, 'w', encoding='utf-8') as f:
    f.write(content)

print('Updated CORS to support credentials')
