class Boxer<T>
{
    public T Id { get; set; }
    public string Name { get; set; }
    public Boxer(T id, string name)
    {
        Id = id; 
        Name = name;
    }
}