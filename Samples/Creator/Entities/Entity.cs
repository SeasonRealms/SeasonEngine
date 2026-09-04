
namespace Creator.Entities;

internal class Entity
{
    internal long ID { get; set; }

    internal string Title { get; set; }

    internal string Desc { get; set; }

    internal string Image { get; set; }

    internal DateTime? Begin { get; set; }

    internal DateTime? Last { get; set; }
}

internal class Chat : Entity
{
    internal string Folder { get; set; }
}

internal class Folder : Entity
{

}

internal class Task : Entity
{

}
