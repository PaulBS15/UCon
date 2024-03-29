using System;

namespace UCon {
   class BaseDimensionsDontMatchException : Exception {
      public Unit ToUnit { get; }
      public Unit FromUnit { get; }

      public BaseDimensionsDontMatchException(Unit ToUnit, Unit FromUnit) : base($"To Unit Base Dimensions ({ToUnit.BaseDimensions()}) do not match From Unit ({FromUnit.BaseDimensions()})") {
         this.ToUnit = ToUnit;
         this.FromUnit = FromUnit;
      }
   }
}
