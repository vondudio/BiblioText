#nullable enable

using System;

namespace AIDevGallery.Sample.Models;

public sealed class Location
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    public DateTime CreatedAt { get; set; }
}
