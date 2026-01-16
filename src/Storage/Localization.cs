// Copyright (c) SeasonEngine and contributors.
// Licensed under the MIT License.
// https://github.com/SeasonRealms/SeasonEngine

namespace Season.Storage;

public static class Localization
{
    public static string Translate(string word, Words words, string lan)
    {
        string result = word;

        if (words?.Lans?.Count > 0)
        {
            var index = words.Lans.IndexOf(lan);

            if (index < 0)
            {

            }
            else
            {
                var wordLi = words.List.FirstOrDefault(li => li.ID.ToLower() == word.ToLower());

                if (wordLi is null)
                {

                }
                else if (wordLi.Values[index] is null or "")
                {

                }
                else
                {
                    result = wordLi.Values[index];
                }
            }
        }

        return result;
    }
}

public class Words
{
    public List<string> Lans { get; set; }

    public List<Word> List { get; set; }

    public string Lan { get; set; }
}

public class Word
{
    public string ID { get; set; }

    public List<string> Values { get; set; }
}
