using System.Text;

namespace bOps.Packages.Scheduler.Core;

public enum CronSourceForm { UserCrontab, SystemCrontab }
public sealed record CronEntry(string? Schedule, string Command, string? User, bool IsValid, string? Warning, bool Truncated=false);
public sealed record CronParseResult(IReadOnlyList<CronEntry> Entries, bool Complete, bool Truncated, IReadOnlyList<string> Warnings);

public static class CronParser
{
    private static readonly HashSet<string> Aliases=["@yearly","@annually","@monthly","@weekly","@daily","@midnight","@hourly","@reboot"];
    public static CronParseResult Parse(string text,CronSourceForm form,int maxCommandCharacters=4096)
    {
        ArgumentNullException.ThrowIfNull(text); ArgumentOutOfRangeException.ThrowIfLessThan(maxCommandCharacters,1);
        var entries=new List<CronEntry>(); var warnings=new List<string>(); var complete=true; var truncated=false; var lineNo=0;
        foreach(var raw in text.Replace("\r\n","\n",StringComparison.Ordinal).Split('\n'))
        {
            lineNo++; var line=raw.Trim(); if(line.Length==0||line.StartsWith('#')) continue; if(IsEnvironment(line)) continue;
            var tokens=Tokens(line); var alias=tokens.Count>0&&tokens[0].Value.StartsWith('@'); var fieldCount=alias?1:5; var required=fieldCount+(form==CronSourceForm.SystemCrontab?1:0);
            if(tokens.Count<required+1) { var warning=$"line {lineNo}: malformed cron entry."; complete=false; warnings.Add(warning); entries.Add(new(null,Bound(line,maxCommandCharacters),null,false,warning,line.Length>maxCommandCharacters)); if(line.Length>maxCommandCharacters) truncated=true; continue; }
            string schedule; string? user=null; var commandToken=form==CronSourceForm.SystemCrontab?required:fieldCount; var commandStart=tokens[commandToken].Start;
            if(alias)
            { schedule=tokens[0].Value.ToLowerInvariant(); if(!Aliases.Contains(schedule)) { var warning=$"line {lineNo}: unsupported alias."; complete=false; warnings.Add(warning); entries.Add(new(null,Bound(line,maxCommandCharacters),form==CronSourceForm.SystemCrontab?tokens[1].Value:null,false,warning,line.Length>maxCommandCharacters)); if(line.Length>maxCommandCharacters) truncated=true; continue; } if(form==CronSourceForm.SystemCrontab) user=tokens[1].Value; }
            else
            { var fields=tokens.Take(5).Select(x=>x.Value).ToArray(); if(!fields.All(IsField)) { var warning=$"line {lineNo}: unsupported or malformed cron field."; complete=false; warnings.Add(warning); entries.Add(new(null,Bound(line,maxCommandCharacters),form==CronSourceForm.SystemCrontab?tokens[5].Value:null,false,warning,line.Length>maxCommandCharacters)); if(line.Length>maxCommandCharacters) truncated=true; continue; } schedule=string.Join(' ',fields); if(form==CronSourceForm.SystemCrontab) user=tokens[5].Value; }
            var command=line[commandStart..]; var wasTruncated=false; if(command.Length>maxCommandCharacters) { command=command[..maxCommandCharacters]; wasTruncated=true; truncated=true; complete=false; }
            entries.Add(new(schedule,command,user,true,null,wasTruncated));
        }
        return new(entries,complete,truncated,warnings);
    }
    private static List<(string Value,int Start)> Tokens(string line) { var result=new List<(string,int)>(); var i=0; while(i<line.Length) { while(i<line.Length&&char.IsWhiteSpace(line[i])) i++; if(i>=line.Length) break; var start=i; while(i<line.Length&&!char.IsWhiteSpace(line[i])) i++; result.Add((line[start..i],start)); } return result; }
    private static string Bound(string value,int maximum) => value.Length<=maximum?value:value[..maximum];
    private static bool IsEnvironment(string line) { var eq=line.IndexOf('='); if(eq<=0) return false; var name=line[..eq].Trim(); return name.All(c=>char.IsLetterOrDigit(c)||c=='_') && (char.IsLetter(name[0])||name[0]=='_'); }
    private static bool IsField(string value) { if(value is "?" or "") return false; foreach(var token in value.Split(',')) { var parts=token.Split('/'); if(parts.Length>2||parts.Any(p=>p.Length==0)||parts.Skip(1).Any(p=>!int.TryParse(p,out var step)||step<=0)) return false; var bounds=parts[0].Split('-'); if(parts[0]!="*"&&(bounds.Length>2||bounds.Any(p=>!int.TryParse(p,out _)))) return false; } return true; }
}

public static class CronStableId
{
    public static string Create(string sourceClass,string sourceIdentity,string? owner,string normalizedSchedule,string normalizedCommand,int duplicateOrdinal)
    { ArgumentOutOfRangeException.ThrowIfLessThan(duplicateOrdinal,1); using var ms=new MemoryStream(); using var bw=new BinaryWriter(ms,Encoding.UTF8,leaveOpen:true); foreach(var value in new[]{sourceClass,sourceIdentity,owner??string.Empty,normalizedSchedule,normalizedCommand}) { var bytes=Encoding.UTF8.GetBytes(value); bw.Write(bytes.Length); bw.Write(bytes); } bw.Write(duplicateOrdinal); bw.Flush(); return "cron:"+Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(ms.ToArray())).ToLowerInvariant(); }
}
