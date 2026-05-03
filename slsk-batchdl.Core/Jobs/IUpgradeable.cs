using System.Collections.Generic;

namespace Sldl.Core.Jobs;
    public interface IUpgradeable
    {
        IEnumerable<Job> Upgrade(bool album, bool aggregate);
    }
