using System.Collections.Generic;

namespace Sockseek.Core.Jobs;
    public interface IUpgradeable
    {
        IEnumerable<Job> Upgrade(bool album, bool group);
    }
